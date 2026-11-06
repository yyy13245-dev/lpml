using LPML.App.Services;
using LPML.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.IO;

namespace LPML.App.Views;

public partial class LogPage : UserControl, IRefreshablePage
{
    public LauncherContext Context { get; }

    public LogPage(LauncherContext context)
    {
        Context = context;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Log.Service.EntryAdded += OnEntryAdded;
            Refresh();
        };
        Unloaded += (_, _) => Log.Service.EntryAdded -= OnEntryAdded;
    }

    private void OnEntryAdded(object? sender, LogEntry entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText(entry + Environment.NewLine);
            LogBox.ScrollToEnd();
            CountText.Text = Lang.F("String.Msg.Log.EntryCount", Log.Service.Snapshot().Count);
        });
    }

    public void Refresh()
    {
        var entries = Log.Service.Snapshot();
        LogBox.Text = string.Join(Environment.NewLine, entries.Select(e => e.ToString()));
        LogBox.ScrollToEnd();
        CountText.Text = Lang.F("String.Msg.Log.EntryCount", entries.Count);
    }

    private void Reload_OnClick(object sender, RoutedEventArgs e) => Refresh();

    private void Clear_OnClick(object sender, RoutedEventArgs e)
    {
        Log.Service.Clear();
        Refresh();
    }

    private void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = Lang.T("String.Msg.Log.FileFilter"), FileName = "lpml-export.log" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, Log.Service.ExportText(), FileLogSink.LogEncoding);
    }

    private void OpenDir_OnClick(object sender, RoutedEventArgs e)
    {
        Core.Utils.FileHelper.EnsureDirectory(Context.Paths.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", Context.Paths.LogDirectory) { UseShellExecute = true });
    }
}
