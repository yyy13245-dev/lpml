using LPML.App.Services;
using LPML.Core;
using LPML.Core.Instances;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LPML.App.Windows;

namespace LPML.App.Views;

public partial class InstancesPage : UserControl, IRefreshablePage
{
    public LauncherContext Context { get; }

    public InstancesPage(LauncherContext context)
    {
        Context = context;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        InstanceList.ItemsSource = null;
        InstanceList.ItemsSource = Context.Instances.Instances;

        NewVersionCombo.Items.Clear();
        foreach (var version in Context.InstalledVersions()) NewVersionCombo.Items.Add(version);
        if (NewVersionCombo.Items.Count > 0) NewVersionCombo.SelectedIndex = 0;

        IsolationCheck.IsChecked = Context.Settings.Paths.InstanceIsolation;

        if (Context.Instances.Instances.Count > 0 && InstanceList.SelectedIndex < 0)
            InstanceList.SelectedIndex = 0;
    }

    private GameInstance? Current => InstanceList.SelectedItem as GameInstance;

    private void InstanceList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中即写入「当前实例」，开始游戏页与设置页都用这一份
        if (Current is not null) Context.Settings.SelectedInstanceId = Current.Id;
    }

    /// <summary>双击直接打开完整实例设置。</summary>
    private void InstanceList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Current is null) return;
        if (App.Current.MainWindow is MainWindow window) OpenInstanceSettings(window);
    }

    private void OpenSettings_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is null)
        {
            Notice.Info(Lang.T("String.Msg.Instance.SelectFirst"));
            return;
        }
        OpenInstanceSettings(App.Current.MainWindow as MainWindow);
    }

    private void OpenInstanceSettings(MainWindow? window)
    {
        if (Current is null || window is null) return;
        window.Navigate("instancesettings");
        if (window.FindPage("instancesettings") is InstanceSettingsPage page)
            page.SetInstance(Current);
    }

    
    private void Create_OnClick(object sender, RoutedEventArgs e)
    {
        var version = NewVersionCombo.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(version))
        {
            Notice.Info(Lang.T("String.Msg.Instance.NeedVersion"));
            return;
        }
        var name = string.IsNullOrWhiteSpace(NewNameBox.Text) ? version : NewNameBox.Text.Trim();
        var instance = Context.Instances.Create(name, version, IsolationCheck.IsChecked == true);
        Context.Settings.SelectedInstanceId = instance.Id;
        Context.Save();
        Refresh();
        InstanceList.SelectedItem = instance;
    }

    private void Clone_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is null) return;
        var copy = Context.Instances.Clone(Current);
        Refresh();
        InstanceList.SelectedItem = copy;
    }

    private void Delete_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is null) return;
        if (!Notice.Ask(Lang.F("String.Msg.Instance.ConfirmDelete", Current.Name))) return;
        Context.Instances.Delete(Current, true);
        Refresh();
    }

    private void OpenDir_OnClick(object sender, RoutedEventArgs e)
    {
        if (Current is null) return;
        var path = Context.Instances.InstanceDirectory(Current);
        Core.Utils.FileHelper.EnsureDirectory(path);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn("UI", Lang.T("String.Msg.Common.OpenDirFailed") + ex.Message);
        }
    }
}
