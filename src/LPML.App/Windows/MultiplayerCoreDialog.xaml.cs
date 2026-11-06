using LPML.App.Services;
using LPML.Core;
using System.Windows;

namespace LPML.App.Windows;

public sealed partial class MultiplayerCoreDialog : Window
{
    private readonly LauncherContext _ctx;

    public MultiplayerCoreDialog(LauncherContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        UrlBox.Text = ctx.Settings.Terracotta.PackageUrl;
        ChecksumBox.Text = ctx.Settings.Terracotta.ExpectedChecksum;
        VerifyCheck.IsChecked = ctx.Settings.Terracotta.VerifyChecksum;
    }

    private void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        var settings = _ctx.Settings.Terracotta;
        settings.PackageUrl = UrlBox.Text.Trim();
        settings.ExpectedChecksum = ChecksumBox.Text.Trim().ToUpperInvariant();
        settings.VerifyChecksum = VerifyCheck.IsChecked ?? true;
        _ctx.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
