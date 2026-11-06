using LPML.Core;
using LPML.Core.Auth;
using LPML.Core.Config;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LPML.App.Services;
using LPML.App.Windows;

namespace LPML.App.Views;

public partial class AccountsPage : UserControl, IParameterizedPage, IRefreshablePage
{
    private string? _verifyUri;

    /// <summary>刷新列表时同步选中项，这时不要当成"用户切换"再存一次。</summary>
    private bool _switching;
    public LauncherContext Context { get; }
    private CancellationTokenSource? _loginCts;

    public AccountsPage(LauncherContext context)
    {
        Context = context;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        AccountList.ItemsSource = null;
        AccountList.ItemsSource = Context.Accounts.Accounts;

        // 让列表的选中项就是当前用户（选中态即当前状态，一眼能看出来）
        _switching = true;
        try
        {
            AccountList.SelectedItem = Context.Accounts.Current(Context.Settings.SelectedAccountId);
        }
        finally { _switching = false; }

        ClientIdBox.Text = Context.Settings.Account.MicrosoftClientId ?? "";
        LittleSkinServerBox.Text = Context.Settings.Account.LittleSkinServer;

        var portable = Context.Accounts.Accounts.Count(a => a.StorageLocation == AccountStorageLocation.Portable);
        var userProfile = Context.Accounts.Accounts.Count(a => a.StorageLocation == AccountStorageLocation.UserProfile);
        var custom = Context.Accounts.Accounts.Count(a => a.StorageLocation == AccountStorageLocation.Custom);
        var defaultName = Context.Settings.Account.StorageLocation switch
        {
            AccountStorageLocation.UserProfile => Lang.T("String.Account.StorageUserProfile"),
            AccountStorageLocation.Custom => Lang.T("String.Msg.Account.LocationCustom"),
            _ => Lang.T("String.Msg.Account.LocationPortable")
        };
        StorageText.Text = Lang.F("String.Msg.Account.Summary",
            Context.Accounts.Accounts.Count, portable, userProfile, custom, defaultName);
    }

    private void MoveAccount_OnClick(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not Account account) return;

        var location = (AccountStorageLocation)Math.Max(0, MoveStorageCombo.SelectedIndex);
        string? directory = null;

        if (location == AccountStorageLocation.Custom)
        {
            var dialog = new OpenFolderDialogCompat(Lang.T("String.Msg.Account.PickCustomDir"));
            if (dialog.Show(Context.Accounts.PortableDirectory) is not string path) return;
            directory = path;
        }

        if (!Context.Accounts.Move(account, location, directory)) return;

        Context.Save();
        Refresh();
        Notice.Info(Lang.F("String.Msg.Account.Moved", account.UserName, Context.Accounts.DirectoryFor(account)));
    }

    private void ImportCustom_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialogCompat(Lang.T("String.Msg.Account.PickImportDir"));
        if (dialog.Show(Context.Accounts.PortableDirectory) is not string path) return;

        var count = Context.Accounts.ImportCustomDirectory(path);
        Context.Save();
        Refresh();
        Notice.Info(Lang.F("String.Msg.Account.Imported", path, count));
    }

    private void OpenStorageDir_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Context.Paths.AccountDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Context.Paths.AccountDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Account", "Open account folder failed: " + ex.Message);
        }
    }

    public void ApplyArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return;
        var parts = arguments.Split('=', 2);
        if (parts.Length != 2 || parts[0] != "tab") return;
        var index = parts[1] switch
        {
            "microsoft" => 0,
            "offline" => 1,
            "yggdrasil" => 2,
            "littleskin" => 3,
            _ => 0
        };
        if (index < AccountTabs.Items.Count) AccountTabs.SelectedIndex = index;
    }

    private void AccountList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中即切换：不用再点"设为当前"，也不弹二次确认，选中态就代表当前用户
        if (_switching) return;
        if (AccountList.SelectedItem is not Account account) return;

        Context.Settings.SelectedAccountId = account.Id;
        Context.Save();

        // 侧边栏 / 开始游戏页的账户显示也要跟着变
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (var window in Application.Current.Windows)
                    if (window is MainWindow main) { main.RefreshAccount(); break; }
            });
        }
        catch (Exception) { /* 刷新失败不影响切换结果 */ }
    }

    private async void RefreshToken_OnClick(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not Account account) return;
        await Context.Accounts.RefreshAllAsync();
        Notice.Info(Lang.T("String.Msg.Account.Refreshed"));
    }

    private void Remove_OnClick(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not Account account) return;
        Context.Accounts.Remove(account);
        Refresh();
    }

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

            // 验证码由服务结构化回传，不再从本地化文本里解析
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
            CodePanel.Visibility = Visibility.Collapsed;
            Refresh();
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
            Process.Start(new ProcessStartInfo(_verifyUri) { UseShellExecute = true });
        }
        catch { }
    }

    private void AddOffline_OnClick(object sender, RoutedEventArgs e)
    {
        var name = OfflineNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Notice.Info(Lang.T("String.Msg.Account.NeedPlayerName"));
            return;
        }
        // 自定义 UUID：留空则按官方规则生成
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
        Refresh();
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

    /// <summary>随机生成一个可用 UUID（固定位已按协议修正）。</summary>
    private void RandomUuid_OnClick(object sender, RoutedEventArgs e)
    {
        var hex = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
        OfflineUuidBox.Text = LPML.Core.Utils.OfflineUuidHelper.Normalize(hex);
    }

    /// <summary>清空 → 回到按用户名自动生成。</summary>
    private void ResetUuid_OnClick(object sender, RoutedEventArgs e) => OfflineUuidBox.Text = "";

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
            Refresh();
            Notice.Info(Lang.F("String.Msg.Account.LoginSuccess", account.UserName));
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
                var provider = Context.Mirrors.For(Core.Config.ResourceCategory.AuthlibInjector);
                var metaUrl = provider.AuthlibInjectorLatestUrl();
                var json = await Context.Http.GetStringAsync(metaUrl);
                var match = System.Text.RegularExpressions.Regex.Match(json, "\"download_url\"\\s*:\\s*\"(?<url>[^\"]+)\"");
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
