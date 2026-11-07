using LPML.Core;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace LPML.App.Views;

public partial class AboutPage : UserControl, IRefreshablePage
{
    public LauncherContext Context { get; }

    public AboutPage(LauncherContext context)
    {
        Context = context;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        RootText.Text = Context.Paths.RootDirectory;
        GameText.Text = Context.Paths.GameDirectory;
        CacheText.Text = Context.Paths.CacheDirectory;
        LogText.Text = Context.Paths.LogDirectory;
    }

    private void OpenRoot_OnClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", Context.Paths.RootDirectory) { UseShellExecute = true });
    }

    private void ViewLicense_OnClick(object sender, RoutedEventArgs e)
    {
        var license = System.IO.Path.Combine(Context.Paths.RootDirectory, "LICENSE");
        if (System.IO.File.Exists(license))
        {
            Process.Start(new ProcessStartInfo(license) { UseShellExecute = true });
        }
        else
        {
            Process.Start(new ProcessStartInfo("explorer.exe", Context.Paths.RootDirectory) { UseShellExecute = true });
        }
    }
}
