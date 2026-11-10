using System;
using System.Windows;
using System.Windows.Threading;

namespace LPML.App.Windows;

/// <summary>一张悬浮提示卡：转圈表示进行中，收起时换成 ✓ / ! 并自动消失。</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _autoClose;

    public ToastWindow(string text)
    {
        InitializeComponent();
        Text.Text = text;
        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); Close(); };
        Loaded += (_, _) => LPML.App.Services.ToastService.RepositionLoaded();
        Closed += (_, _) => { _autoClose.Stop(); LPML.App.Services.ToastService.Remove(this); };
    }

    public void Dismiss()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Close); return; }
        Close();
    }

    /// <summary>操作结束：转圈换成结果图标，短暂停留后自己关掉。</summary>
    public void MarkDone(bool success)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => MarkDone(success)); return; }
        Ring.Visibility = Visibility.Collapsed;
        Glyph.Visibility = Visibility.Visible;
        Glyph.Text = success ? "✓" : "!";
        _autoClose.Start();
    }
}
