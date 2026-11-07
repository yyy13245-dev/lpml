using LPML.App.Services;
using LPML.Core;
using LPML.Core.Multiplayer;
using System.Windows;

namespace LPML.App.Windows;

public sealed partial class MultiplayerCoreDialog : Window
{
    private readonly LauncherContext _ctx;

    public MultiplayerCoreDialog(LauncherContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();

        var s = ctx.Settings.Terracotta;
        UrlBox.Text = s.PackageUrl;
        VersionBox.Text = s.VersionOverride;
        ChecksumBox.Text = s.ExpectedChecksum;
        VerifyCheck.IsChecked = s.VerifyChecksum;
        MetadataCheck.IsChecked = s.UseOnlineMetadata;
        ExternalBox.Text = s.ExternalCorePath ?? "";
        NodesBox.Text = string.Join("\n", s.CustomPublicNodes ?? new System.Collections.Generic.List<string>());
        FixedCodeBox.Text = s.FixedRoomCode;
        PlayerBox.Text = s.PlayerName;
    }

    private void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        var s = _ctx.Settings.Terracotta;
        s.PackageUrl = UrlBox.Text.Trim();
        s.VersionOverride = VersionBox.Text.Trim();
        s.ExpectedChecksum = ChecksumBox.Text.Trim().ToUpperInvariant();
        s.VerifyChecksum = VerifyCheck.IsChecked ?? true;
        s.UseOnlineMetadata = MetadataCheck.IsChecked ?? true;
        s.ExternalCorePath = ExternalBox.Text.Trim();
        s.CustomPublicNodes = ParseNodes(NodesBox.Text);
        s.FixedRoomCode = NormalizeFixedCode(FixedCodeBox.Text);
        s.PlayerName = PlayerBox.Text.Trim();
        _ctx.Save();
        DialogResult = true;
        Close();
    }

    private static System.Collections.Generic.List<string> ParseNodes(string text)
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var node = line.Trim();
            if (!string.IsNullOrWhiteSpace(node)) list.Add(node);
        }
        return list;
    }

    /// <summary>固定邀请码统一成官方格式；留空表示由陶瓦每次生成。</summary>
    private static string NormalizeFixedCode(string text)
        => string.IsNullOrWhiteSpace(text) ? "" : TerracottaCore.NormalizeRoomCode(text);

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
