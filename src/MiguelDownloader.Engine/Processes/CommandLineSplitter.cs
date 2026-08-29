using System.Text;

namespace MiguelDownloader.Engine.Processes;

/// <summary>
/// Splits a user-typed argument string into separate argv entries.
/// <para>
/// The advanced settings let a user add their own yt-dlp flags. Those arrive as one string, and
/// the only safe way to use them is to split them here and pass the pieces as separate arguments,
/// exactly as the process layer does with everything else. Handing the raw string to a shell
/// would turn a settings field into arbitrary command execution.
/// </para>
/// <para>
/// Quoting follows the Windows convention: double quotes group, a backslash escapes a quote, and
/// doubled quotes inside a quoted run produce a literal quote.
/// </para>
/// </summary>
public static class CommandLineSplitter
{
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];

        var results = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (c == '\\')
            {
                // Count the run of backslashes; they only escape when a quote follows.
                var slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { slashes++; i++; }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        current.Append('"');   // escaped quote, stays literal
                        hasContent = true;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                        hasContent = true;
                    }
                }
                else
                {
                    current.Append('\\', slashes);
                    if (slashes > 0) hasContent = true;
                    i--;                        // reprocess the non-backslash character
                }
                continue;
            }

            if (c == '"')
            {
                // A doubled quote inside a quoted run means one literal quote.
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                hasContent = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasContent)
                {
                    results.Add(current.ToString());
                    current.Clear();
                    hasContent = false;
                }
                continue;
            }

            current.Append(c);
            hasContent = true;
        }

        if (hasContent) results.Add(current.ToString());

        return results;
    }
}
