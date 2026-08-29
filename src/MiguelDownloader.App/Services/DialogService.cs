using System.Windows;
using MiguelDownloader.App.Localization;
using MiguelDownloader.Core.Settings;
using Microsoft.Win32;
using System.IO;

namespace MiguelDownloader.App.Services;

/// <summary>What the user chose when a file already existed.</summary>
public enum FileConflictChoice
{
    Cancel = 0,
    Replace,
    KeepBoth,
    Skip,
}

/// <summary>
/// Modal prompts and file pickers.
/// <para>
/// Anything that could destroy data asks first and defaults to the safe answer, so an accidental
/// Enter key never overwrites a finished download.
/// </para>
/// </summary>
public sealed class DialogService
{
    private static Window? Owner => Application.Current?.Windows
        .OfType<Window>()
        .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    /// <summary>Shows a message with a single acknowledgement button.</summary>
    public void ShowMessage(string message, string? title = null, bool isError = false)
    {
        MessageBox.Show(
            Owner ?? Application.Current!.MainWindow!,
            message,
            title ?? Loc.Get("App_Title"),
            MessageBoxButton.OK,
            isError ? MessageBoxImage.Error : MessageBoxImage.Information);
    }

    /// <summary>Asks a yes/no question. Defaults to "no" so Enter cannot confirm by accident.</summary>
    public bool Confirm(string message, string? title = null)
    {
        var result = MessageBox.Show(
            Owner ?? Application.Current!.MainWindow!,
            message,
            title ?? Loc.Get("App_Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Asks what to do about an existing file.
    /// <para>
    /// "Keep both" is offered before "replace" and is the default, because renaming is reversible
    /// and overwriting somebody's finished download is not.
    /// </para>
    /// </summary>
    public FileConflictChoice AskFileConflict(string fileName)
    {
        var message = string.Format(
            Loc.Culture,
            "{0}\n\n{1}",
            string.Format(Loc.Culture, "\"{0}\"", fileName),
            Loc.Get("Error_FileExists"));

        // Yes = keep both, No = replace, Cancel = skip this item.
        var result = MessageBox.Show(
            Owner ?? Application.Current!.MainWindow!,
            $"{message}\n\n" +
            $"{Loc.Get("Action_Yes")}: {Loc.Get("Settings_ExistingFile_Rename")}\n" +
            $"{Loc.Get("Action_No")}: {Loc.Get("Settings_ExistingFile_Overwrite")}",
            Loc.Get("Dialog_FileExists_Title"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        return result switch
        {
            MessageBoxResult.Yes => FileConflictChoice.KeepBoth,
            MessageBoxResult.No => FileConflictChoice.Replace,
            _ => FileConflictChoice.Cancel,
        };
    }

    /// <summary>Maps a conflict choice onto the policy the download pipeline understands.</summary>
    public static ExistingFilePolicy ToPolicy(FileConflictChoice choice) => choice switch
    {
        FileConflictChoice.Replace => ExistingFilePolicy.Overwrite,
        FileConflictChoice.KeepBoth => ExistingFilePolicy.RenameAutomatically,
        FileConflictChoice.Skip => ExistingFilePolicy.Skip,
        _ => ExistingFilePolicy.Ask,
    };

    /// <summary>
    /// Confirms a bulk download. A channel can hold thousands of videos, and starting that by
    /// accident would be both slow to notice and tedious to undo.
    /// </summary>
    public bool ConfirmBulkDownload(int itemCount)
        => Confirm(
            Loc.Format("Dialog_BulkConfirm_Message", itemCount),
            Loc.Get("Dialog_BulkConfirm_Title"));

    /// <summary>Confirms closing while downloads are still running.</summary>
    public bool ConfirmExitWithActiveDownloads(int activeCount)
        => Confirm(
            Loc.Format("Dialog_ExitConfirm_Message", activeCount),
            Loc.Get("Dialog_ExitConfirm_Title"));

    /// <summary>Picks a folder. Returns null when the user cancels.</summary>
    public string? PickFolder(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    /// <summary>Picks an executable, used for the tool path overrides.</summary>
    public string? PickExecutable(string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executáveis (*.exe)|*.exe|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null,
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    /// <summary>Picks where to write the diagnostics bundle.</summary>
    public string? PickSaveFile(string suggestedName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = filter,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}
