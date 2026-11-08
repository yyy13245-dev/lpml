using LPML.App.Services;
using LPML.Core;
using LPML.Core.Instances;
using LPML.Core.Mods;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using LPML.App.Windows;

namespace LPML.App.Views;

public partial class ModsPage : UserControl, IRefreshablePage
{
    public LauncherContext Context { get; }

    private List<ModInfo> _mods = new();
    private bool _multiSelect;
    private bool _suppressSelectEvents;
    private ModInfo? _contextMod;

    public ModsPage(LauncherContext context)
    {
        Context = context;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        // 同一个 List 引用不会触发 ComboBox 刷新，新实例就一直看不到
        InstanceCombo.ItemsSource = null;
        var instances = Context.InstancesInCurrentFolder();
        InstanceCombo.ItemsSource = instances;
        var selected = Context.Instances.Find(Context.Settings.SelectedInstanceId) ?? instances.FirstOrDefault();
        if (selected is not null) InstanceCombo.SelectedItem = selected;
        Reload();
    }

    /// <summary>跳到下载模组页，并预选当前这个实例（进页面后仍可改选别的实例）。</summary>
    private void DownloadMods_OnClick(object sender, RoutedEventArgs e)
    {
        var instance = InstanceCombo.SelectedItem as GameInstance;
        if (Window.GetWindow(this) is MainWindow window) window.OpenModDownloadFor(instance);
    }

    private void InstanceCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ExitMultiSelect();
        Reload();
    }

    private void Reload()
    {
        if (InstanceCombo.SelectedItem is not GameInstance instance)
        {
            ModList.ItemsSource = null;
            _mods = new List<ModInfo>();
            ModsDirText.Text = "";
            SummaryText.Text = Lang.T("String.Msg.Mod.NeedInstance");
            return;
        }
        var directory = Context.ModsDirectoryFor(instance);
        Core.Utils.FileHelper.EnsureDirectory(directory);
        ModsDirText.Text = directory;
        _mods = ModManager.Scan(directory);
        ModList.ItemsSource = _mods;
        SummaryText.Text = Lang.F("String.Msg.Mod.Summary", _mods.Count, _mods.Count(m => m.Enabled), _mods.Count(m => !m.Enabled));
        UpdateMultiBar();
    }

    private void ModToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ModInfo mod }) return;
        ModManager.SetEnabled(mod, mod.Enabled == false);
        Reload();
    }

    private void DeleteMod_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModInfo mod }) return;
        DeleteOne(mod);
    }

    private void DeleteOne(ModInfo mod)
    {
        if (!Notice.Ask(Lang.F("String.Msg.Mod.ConfirmDelete", mod.DisplayName))) return;
        ModManager.Delete(mod);
        ExitMultiSelect();
        Reload();
    }

    private void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        ExitMultiSelect();
        Reload();
    }

    private void AddMod_OnClick(object sender, RoutedEventArgs e)
    {
        if (InstanceCombo.SelectedItem is not GameInstance instance) return;
        var dialog = new OpenFileDialog
        {
            Filter = Lang.T("String.Msg.Mod.Filter"),
            Title = Lang.T("String.Msg.Mod.Pick"),
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;
        var directory = Context.ModsDirectoryFor(instance);
        Core.Utils.FileHelper.EnsureDirectory(directory);
        foreach (var file in dialog.FileNames)
        {
            try { File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true); }
            catch (IOException ex) { Log.Warn("Mods", Lang.T("String.Msg.Mod.CopyFailed") + ex.Message); }
        }
        Reload();
    }

    private void OpenDir_OnClick(object sender, RoutedEventArgs e)
    {
        if (InstanceCombo.SelectedItem is not GameInstance instance) return;
        var path = Context.ModsDirectoryFor(instance);
        Core.Utils.FileHelper.EnsureDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    // ── 多选 ───────────────────────────────────────────────

    private void ModSelect_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectEvents) return;
        _multiSelect = true;
        MultiBar.Visibility = Visibility.Visible;
        UpdateMultiBar();
    }

    private void EnterMultiSelectSilently()
    {
        _multiSelect = true;
        MultiBar.Visibility = Visibility.Visible;
    }

    private void ExitMultiSelect()
    {
        _suppressSelectEvents = true;
        foreach (var mod in _mods) mod.IsSelected = false;
        _suppressSelectEvents = false;
        _multiSelect = false;
        MultiBar.Visibility = Visibility.Collapsed;
        UpdateSelectAllButton();
    }

    private void UpdateMultiBar()
    {
        if (!_multiSelect) return;
        var count = _mods.Count(m => m.IsSelected);
        MultiCountText.Text = Lang.F("String.Mod.SelectedCount", count);
        UpdateSelectAllButton();
    }

    private void UpdateSelectAllButton()
    {
        var all = _mods.Count > 0 && _mods.All(m => m.IsSelected);
        SelectAllButton.Content = Lang.T(all ? "String.Mod.UnselectAll" : "String.Mod.SelectAll");
    }

    private void SelectAll_OnClick(object sender, RoutedEventArgs e)
    {
        var all = _mods.Count > 0 && _mods.All(m => m.IsSelected);
        _suppressSelectEvents = true;
        foreach (var mod in _mods) mod.IsSelected = !all;
        _suppressSelectEvents = false;
        EnterMultiSelectSilently();
        UpdateMultiBar();
    }

    private void CancelMulti_OnClick(object sender, RoutedEventArgs e) => ExitMultiSelect();

    private void BatchEnable_OnClick(object sender, RoutedEventArgs e) => SetEnabledForSelected(true);

    private void BatchDisable_OnClick(object sender, RoutedEventArgs e) => SetEnabledForSelected(false);

    private void SetEnabledForSelected(bool enabled)
    {
        var picked = _mods.Where(m => m.IsSelected).ToList();
        if (picked.Count == 0) { Notice.Info(Lang.T("String.Mod.NothingSelected")); return; }
        foreach (var mod in picked) ModManager.SetEnabled(mod, enabled);
        ExitMultiSelect();
        Reload();
    }

    private void BatchDelete_OnClick(object sender, RoutedEventArgs e)
    {
        var picked = _mods.Where(m => m.IsSelected).ToList();
        if (picked.Count == 0) { Notice.Info(Lang.T("String.Mod.NothingSelected")); return; }
        if (!Notice.Ask(Lang.F("String.Mod.ConfirmBatchDelete", picked.Count))) return;
        foreach (var mod in picked) ModManager.Delete(mod);
        ExitMultiSelect();
        Reload();
    }

    // ── 右键菜单 ───────────────────────────────────────────

    private void ModList_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // ContextMenu 不在主可视化树，DynamicResource 可能取不到；这里按当前语言兜底刷新
        ContextDeleteItem.Header = Lang.T("String.Mod.ContextDelete");
        _contextMod = null;
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null && node is not ListBoxItem)
            node = VisualTreeHelper.GetParent(node);
        if (node is ListBoxItem { DataContext: ModInfo mod }) _contextMod = mod;
        if (_contextMod is null) e.Handled = true;   // 空白处不弹
    }

    private void ContextDelete_OnClick(object sender, RoutedEventArgs e)
    {
        if (_contextMod is null) return;
        DeleteOne(_contextMod);
        _contextMod = null;
    }
}
