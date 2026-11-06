using System.IO;
using Forms = System.Windows.Forms;

namespace LPML.App;

/// <summary>文件夹选择对话框（Win32 FolderBrowserDialog 的轻量封装）。</summary>
public sealed class OpenFolderDialogCompat
{
    private readonly string _description;

    public OpenFolderDialogCompat(string description)
    {
        _description = description;
    }

    public string? Show(string? initialDirectory = null)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.SelectedPath = initialDirectory;
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}
