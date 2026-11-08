using LPML.App.Services;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Auth;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LPML.App.Views;

/// <summary>
/// 账户登录小窗口：微软 / 离线 / 外置 / LittleSkin 四种方式各自一页，
/// 由账户页左侧导航按钮按 kind 弹出，主页面始终保留完整的账户列表。
/// </summary>
public partial class AccountLoginWindow : Window
{
    private LauncherContext Context => App.Context;
    private readonly string _kind;
    private string? _verifyUri;
    private CancellationTokenSource? _loginCts;

    public AccountLoginWindow(string kind)
    {
        _kind = kind;
        InitializeComponent();
        Owner = Application.Current.MainWindow;

        TitleText.Text = kind switch
        {
            "offline" => Lang.T("String.Account.TabOffline"),
            "yggdrasil" => Lang.T("String.Account.TabYggdrasil"),
            "littleskin" => Lang.T("String.Account.LittleSkinTitle"),
            _ => Lang.T("String.Account.TabMicrosoft")
        };
        PaneMicrosoft.Visibility = kind == "microsoft" ? Visibility.Visible : Visibility.Collapsed;
        PaneOffline.Visibility = kind == "offline" ? Visibility.Visible : Visibility.Collapsed;
        PaneYggdrasil.Visibility = kind == "yggdrasil" ? Visibility.Visible : Visibility.Collapsed;
        PaneLittleSkin.Visibility = kind == "littleskin" ? Visibility.Visible : Visibility.Collapsed;

        ClientIdBox.Text = Context.Settings.Account.MicrosoftClientId ?? "";
        LittleSkinServerBox.Text = Context.Settings.Account.LittleSkinServer;
        OfflineUuidHint.Text = Lang.T("String.Account.UuidAutoHint");

        Closed += (_, _) => _loginCts?.Cancel();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void RefreshMainAccountPage()
    {
        if (Application.Current.MainWindow is MainWindow main) main.RefreshAccountsPage();
    }

    // ── 微软 ──────────────────────────────────────────────
    private void ClientIdBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        Context.Settings.Account.MicrosoftClientId = ClientIdBox.Text.Trim();
        Context.ApplySettings();
        Context.Save();
    }

    private async void MicrosoftLogin_OnClick(object sender, RoutedEventArgs e)
    {
        Context.Settings.Account.MicrosoftClientId = ClientIdBox.Text.Trim();
        Context.ApplySettings();

        if (string.IsNullOrWhiteSpace(ClientIdBox.Text))
        {
            Notice.Info(Lang.T("String.Msg.Account.NeedClientId"));
            return;
        }

        _loginCts = new CancellationTokenSource();
        CodePanel.Visibility = Visibility.Visible;
        MicrosoftStatus.Text = Lang.T("String.Msg.Account.RequestingCode");
        try
        {
            var progress = new Progress<string>(text => MicrosoftStatus.Text = text);

            void OnUserCode(string code, string uri)
            {
                UserCodeText.Text = code;
                CodeHint.Text = Lang.T("String.Msg.Account.EnterCodeHint");
                try { Clipboard.SetText(code); } catch { }
                _verifyUri = uri;
            }

            var account = await Context.Accounts.LoginMicrosoftAsync(progress, _loginCts.Token, OnUserCode);
            Context.Accounts.Add(account);
            Context.Settings.SelectedAccountId = account.Id;
            Context.Save();
            MicrosoftStatus.Text = Lang.F("String.Msg.Account.LoginSuccess", account.UserName);
            RefreshMainAccountPage();
            Notice.Info(Lang.F("String.Msg.Account.LoginSuccess", account.UserName));
            Close();
        }
        catch (Exception ex)
        {
            MicrosoftStatus.Text = Lang.F("String.Msg.Account.LoginFailed", ex.Message);
            Log.Warn("Account", "Microsoft login failed: " + ex.Message);
        }
    }

    private void OpenVerify_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_verifyUri))
                Process.Start(new ProcessStartInfo(_verifyUri) { UseShellExecute = true });
        }
        catch { }
    }

    // ── 离线 ──────────────────────────────────────────────
    private void AddOffline_OnClick(object sender, RoutedEventArgs e)
    {
        var name = OfflineNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Notice.Info(Lang.T("String.Msg.Account.NeedPlayerName"));
            return;
        }
        var uuid = OfflineUuidBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(uuid) && !LPML.Core.Utils.OfflineUuidHelper.IsValid(uuid))
        {
            Notice.Info(Lang.T("String.Msg.Account.UuidInvalid"));
            return;
        }
        var account = Context.Accounts.CreateOffline(name, string.IsNullOrEmpty(uuid) ? null : uuid);
        Context.Accounts.Add(account);
        Context.Settings.SelectedAccountId = account.Id;
        Context.Save();
        RefreshMainAccountPage();
        Notice.Info(Lang.F("String.Msg.Account.LoginSuccess", account.UserName));
        Close();
    }

    private void OfflineUuid_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (OfflineUuidBox is null || OfflineUuidHint is null) return;
        var text = OfflineUuidBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            OfflineUuidHint.Text = Lang.T("String.Account.UuidAutoHint");
            return;
        }
        OfflineUuidHint.Text = LPML.Core.Utils.OfflineUuidHelper.IsValid(text)
            ? Lang.T("String.Account.UuidFixedHint")
            : Lang.T("String.Msg.Account.UuidInvalid");
    }

    private void RandomUuid_OnClick(object sender, RoutedEventArgs e)
    {
        var hex = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        OfflineUuidBox.Text = LPML.Core.Utils.OfflineUuidHelper.Normalize(hex);
    }

    private void ResetUuid_OnClick(object sender, RoutedEventArgs e) => OfflineUuidBox.Text = "";

    // ── 外置 / LittleSkin ──────────────────────────────────
    private async void YggdrasilLogin_OnClick(object sender, RoutedEventArgs e)
    {
        var server = AuthServerBox.Text.Trim();
        var user = AuthUserBox.Text.Trim();
        var password = AuthPasswordBox.Password;
        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            Notice.Info(Lang.T("String.Msg.Account.NeedServerCreds"));
            return;
        }
        await DoYggdrasilLoginAsync(server, user, password, AccountType.Yggdrasil);
    }

    private async void LittleSkinLogin_OnClick(object sender, RoutedEventArgs e)
    {
        var user = LittleSkinUserBox.Text.Trim();
        var password = LittleSkinPasswordBox.Password;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            Notice.Info(Lang.T("String.Msg.Account.NeedCreds"));
            return;
        }
        await DoYggdrasilLoginAsync(Context.Settings.Account.LittleSkinServer, user, password, AccountType.LittleSkin);
    }

    private async Task DoYggdrasilLoginAsync(string server, string user, string password, AccountType type)
    {
        try
        {
            var account = await Context.Accounts.LoginYggdrasilAsync(server, user, password, type, CancellationToken.None);
            await EnsureAuthlibInjectorAsync();
            Context.Accounts.Add(account);
            Context.Settings.SelectedAccountId = account.Id;
            Context.Save();
            RefreshMainAccountPage();
            Notice.Info(Lang.F("String.Msg.Account.LoginSuccess", account.UserName));
            Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Account", "External login failed: " + ex.Message);
            Notice.Error(Lang.T("String.Msg.Common.LoginFailedTitle"), ex.Message);
        }
    }

    /// <summary>确保 authlib-injector 存在（外置登录注入用），缺失时自动下载到缓存目录。</summary>
    private async Task EnsureAuthlibInjectorAsync()
    {
        var configured = Context.Settings.Account.AuthlibInjectorPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return;

        var target = Path.Combine(Context.Paths.CacheDirectory, "authlib-injector.jar");
        if (!File.Exists(target))
        {
            try
            {
                var provider = Context.Mirrors.For(LPML.Core.Config.ResourceCategory.AuthlibInjector);
                var metaUrl = provider.AuthlibInjectorLatestUrl();
                var json = await Context.Http.GetStringAsync(metaUrl);
                var match = System.Text.RegularExpressions.Regex.Match(json, @"""download_url""\s*:\s*""(?<url>[^""]+)""");
                if (!match.Success) return;
                var url = provider.Rewrite(match.Groups["url"].Value);
                await Context.Http.DownloadToFileAsync(url, target);
            }
            catch (Exception ex)
            {
                Log.Warn("Account", "authlib-injector download failed: " + ex.Message);
                return;
            }
        }
        Context.Settings.Account.AuthlibInjectorPath = target;
        Context.Save();
    }
}
