using LPML.App.Services;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Auth;
using LPML.Core.Config;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LPML.App.Views;

/// <summary>
/// 账户页：只保留一个完整的账户列表大框。
/// 四种登录方式由左侧导航按钮各自弹出的 AccountLoginWindow 完成，不再把表单嵌在本页下方。
/// </summary>
public partial class AccountsPage : UserControl, IParameterizedPage, IRefreshablePage
{
    /// <summary>刷新列表时同步选中项，这时不要当成"用户切换"再存一次。</summary>
    private bool _switching;
    public LauncherContext Context { get; }

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

    /// <summary>登录方式已改为弹窗，账户页不再有分页参数。</summary>
    public void ApplyArguments(string arguments) { }

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
}
