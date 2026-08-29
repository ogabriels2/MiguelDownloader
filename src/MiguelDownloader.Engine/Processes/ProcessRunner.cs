using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Processes;

/// <summary>One line of output from a child process.</summary>
/// <param name="Text">The line, without its terminator.</param>
/// <param name="IsError">True when it came from stderr.</param>
public readonly record struct ProcessLine(string Text, bool IsError);

/// <summary>The outcome of a finished process run.</summary>
public sealed record ProcessResult
{
    public required int ExitCode { get; init; }

    /// <summary>Everything the process wrote to stderr, joined. The basis for error classification.</summary>
    public required string StandardError { get; init; }

    /// <summary>Everything written to stdout, captured only when the caller asked for it.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    public bool Succeeded => ExitCode == 0;
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Runs the external tools.
/// <para>
/// Arguments are always passed as a list and never as a single command line, so a video title
/// containing quotes, ampersands or newlines cannot alter what gets executed. The shell is never
/// involved: <c>UseShellExecute</c> stays false, which also means no command interpreter parses
/// anything the app builds.
/// </para>
/// <para>
/// Standard output and error are both read as UTF-8. YouTube titles routinely contain characters
/// outside the Windows ANSI code page, and reading them with the console default would corrupt
/// file names.
/// </para>
/// </summary>
public sealed class ProcessRunner(ILogger<ProcessRunner> logger)
{
    private readonly ILogger<ProcessRunner> _logger = logger;

    /// <summary>
    /// Runs a process to completion, streaming each output line to <paramref name="onLine"/>.
    /// </summary>
    /// <param name="executablePath">Absolute path to the executable.</param>
    /// <param name="arguments">Arguments as separate entries. Never joined into a command line.</param>
    /// <param name="onLine">
    /// Called for every stdout and stderr line as it arrives. Invoked on a background thread, so
    /// callers marshalling to a UI thread must do so themselves.
    /// </param>
    /// <param name="captureStandardOutput">
    /// Keep stdout in memory for the result. Left off for downloads, whose stdout is progress
    /// chatter, and on for metadata extraction, whose stdout is the JSON payload.
    /// </param>
    /// <param name="workingDirectory">Working directory for the child process.</param>
    /// <param name="environment">Extra environment variables.</param>
    /// <param name="cancellationToken">Cancels the run and terminates the process tree.</param>
    public async Task<ProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        Action<ProcessLine>? onLine = null,
        bool captureStandardOutput = false,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!File.Exists(executablePath))
            throw new FileNotFoundException($"Executable not found: {executablePath}", executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        // ArgumentList quotes each entry correctly for the Win32 command-line parser, which is
        // what makes injection through a title or URL impossible.
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        // Force UTF-8 inside the child too; yt-dlp is a Python program and honours these.
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        if (environment is not null)
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdout = captureStandardOutput ? new StringBuilder(capacity: 1 << 16) : null;
        var stderr = new StringBuilder(capacity: 4096);

        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) =>
        {
            try { exited.TrySetResult(process.ExitCode); }
            catch (InvalidOperationException) { exited.TrySetResult(-1); }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout?.AppendLine(e.Data);
            onLine?.Invoke(new ProcessLine(e.Data, IsError: false));
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // stderr is bounded: a runaway tool must not be able to exhaust memory, but the tail
            // is what matters for diagnosis, so we keep the most recent output.
            AppendBounded(stderr, e.Data);
            onLine?.Invoke(new ProcessLine(e.Data, IsError: true));
        };

        var stopwatch = Stopwatch.StartNew();

        _logger.LogDebug("Starting {Executable} with {Count} arguments", Path.GetFileName(executablePath), arguments.Count);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {executablePath}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Nothing is ever written to the child, but leaving the handle open makes a tool that
        // prompts for input hang forever instead of failing fast.
        try { process.StandardInput.Close(); } catch (IOException) { /* already gone */ }

        await using var registration = cancellationToken.Register(() => TryKill(process));

        var exitCode = await exited.Task.ConfigureAwait(false);

        // Give the async readers a moment to drain what is still buffered after exit.
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidOperationException) { /* already reaped */ }

        stopwatch.Stop();

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug("{Executable} exited with {ExitCode} after {Elapsed}ms",
            Path.GetFileName(executablePath), exitCode, stopwatch.ElapsedMilliseconds);

        return new ProcessResult
        {
            ExitCode = exitCode,
            StandardError = stderr.ToString(),
            StandardOutput = stdout?.ToString() ?? string.Empty,
            Duration = stopwatch.Elapsed,
        };
    }

    private const int MaxStderrChars = 64 * 1024;

    private static void AppendBounded(StringBuilder builder, string line)
    {
        builder.AppendLine(line);
        if (builder.Length <= MaxStderrChars) return;

        // Drop the oldest half, keeping the tail where the actual failure is reported.
        var keep = builder.ToString(builder.Length - MaxStderrChars / 2, MaxStderrChars / 2);
        builder.Clear();
        builder.AppendLine("[...output truncated...]");
        builder.Append(keep);
    }

    /// <summary>
    /// Terminates the process and its children. yt-dlp spawns ffmpeg for merging, so killing only
    /// the parent would leave an ffmpeg holding a lock on the output file.
    /// </summary>
    private void TryKill(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            _logger.LogDebug("Terminated process tree {ProcessId}", process.Id);
        }
        catch (InvalidOperationException) { /* exited between the check and the kill */ }
        catch (NotSupportedException ex) { _logger.LogWarning(ex, "Could not terminate process tree"); }
        catch (System.ComponentModel.Win32Exception ex) { _logger.LogWarning(ex, "Could not terminate process"); }
    }
}
