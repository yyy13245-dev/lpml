using LPML.Core.Config;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ThemeMode = LPML.Core.Config.ThemeMode;

namespace LPML.App;

/// <summary>
/// 主题服务：浅色 / 深色 / 跟随系统，强调色、毛玻璃透明度、字号缩放。
/// 直接改写资源字典中的画刷，界面无需重启即可生效。
/// </summary>
public sealed class ThemeService
{
    public ThemeMode CurrentTheme { get; private set; } = ThemeMode.System;

    /// <summary>true 表示流畅模式：不透明遮罩 + 瞬时反馈。</summary>
    public bool IsSmooth { get; private set; }

    public void Apply(AppearanceSettings appearance)
    {
        IsSmooth = appearance.PerformanceMode == PerformanceMode.Smooth;
        var dark = appearance.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemDark()
        };
        CurrentTheme = appearance.Theme;

        var resources = System.Windows.Application.Current.Resources;
        var accent = ParseColor(appearance.AccentColor, Color.FromRgb(0x7C, 0x6B, 0xFF));
        var glassAlpha = (byte)Math.Clamp(appearance.BackdropOpacity * 255, 20, 255);
        var smooth = IsSmooth;

        void Set(string key, Color color) => resources[key] = new SolidColorBrush(color);
        void SetAlpha(string key, Color baseColor, double alpha) =>
            resources[key] = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), baseColor.R, baseColor.G, baseColor.B));

        // 流畅模式：卡片不再半透明。半透明需要逐帧与背景合成，
        // 背景那层遮罩（Glass）是单张矩形，开销可忽略，仍保留原透明度让壁纸透出来一点。
        // 改成不透明后 WPF 能走不透明渲染路径，这是最大的一项开销来源。
        // 视觉上用"实色 + 描边"替代"透明叠色"，层次仍然分得开。
        byte Card(byte alpha) => smooth ? (byte)0xFF : alpha;

        if (dark)
        {
            Set("Brush.Window", Color.FromRgb(0x14, 0x14, 0x1A));
            Set("Brush.Glass", Color.FromArgb(glassAlpha, 0x1E, 0x1E, 0x28));
            Set("Brush.Card", Color.FromArgb(Card((byte)(glassAlpha * 0.72)), 0x2A, 0x2A, 0x36));
            Set("Brush.CardHover", Color.FromArgb(Card((byte)(glassAlpha * 0.85)), 0x33, 0x33, 0x42));
            Set("Brush.CardPressed", Color.FromArgb(Card(glassAlpha), 0x3D, 0x3D, 0x4E));
            Set("Brush.Elevated", Color.FromArgb(Card(glassAlpha), 0x33, 0x33, 0x3F));
            Set("Brush.Foreground", Color.FromRgb(0xF2, 0xF2, 0xF8));
            Set("Brush.ForegroundSecondary", Color.FromRgb(0xA9, 0xA9, 0xBC));
            Set("Brush.ForegroundMuted", Color.FromRgb(0x7C, 0x7C, 0x90));
            Set("Brush.Border", Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
            Set("Brush.BorderStrong", Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));
            Set("Brush.Track", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            Set("Brush.Window", Color.FromRgb(0xF4, 0xF5, 0xFA));
            Set("Brush.Glass", Color.FromArgb(glassAlpha, 0xFF, 0xFF, 0xFF));
            Set("Brush.Card", Color.FromArgb(Card((byte)(glassAlpha * 0.78)), 0xFF, 0xFF, 0xFF));
            Set("Brush.CardHover", Color.FromArgb(Card((byte)(glassAlpha * 0.9)), 0xEC, 0xED, 0xF6));
            Set("Brush.CardPressed", Color.FromArgb(Card(glassAlpha), 0xE2, 0xE4, 0xF0));
            Set("Brush.Elevated", Color.FromArgb(Card(glassAlpha), 0xFF, 0xFF, 0xFF));
            Set("Brush.Foreground", Color.FromRgb(0x1B, 0x1C, 0x22));
            Set("Brush.ForegroundSecondary", Color.FromRgb(0x55, 0x57, 0x66));
            Set("Brush.ForegroundMuted", Color.FromRgb(0x84, 0x86, 0x96));
            Set("Brush.Border", Color.FromArgb(0x33, 0x00, 0x00, 0x00));
            Set("Brush.BorderStrong", Color.FromArgb(0x44, 0x00, 0x00, 0x00));
            Set("Brush.Track", Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        }

        Set("Brush.Accent", accent);
        SetAlpha("Brush.AccentSoft", accent, 0.22);
        Set("Brush.AccentForeground", IsLight(accent) ? Color.FromRgb(0x14, 0x14, 0x1A) : Colors.White);
        Set("Brush.Success", Color.FromRgb(0x4A, 0xDE, 0x80));
        Set("Brush.Warning", Color.FromRgb(0xFB, 0x92, 0x3C));
        Set("Brush.Danger", Color.FromRgb(0xF8, 0x71, 0x71));

        // 动效：关闭动效或关闭拉伸时，缩放量设为 1（等价于静止）
        var enableMotion = appearance.EnableAnimations;
        var stretch = appearance.EnableStretchAnimation ? appearance.HoverScaleAmount : 0d;
        resources["Scale.Hover"] = enableMotion ? 1.0 + stretch : 1.0;
        resources["Scale.Press"] = enableMotion ? 1.0 + stretch * 2 : 1.0;

        // 交互反馈的补间：流畅模式改用 Styles/Controls.Smooth.xaml 覆盖样式
        // （去掉 Storyboard，改成纯状态切换）。不能靠改 Storyboard 的 Duration 实现 ——
        // 时间线树在 Style 密封时必须可冻结，用 DynamicResource 会直接抛异常。

        // 流畅模式关掉阴影（模糊 + 阴影都是逐像素开销）
        resources["Effect.Card"] = smooth ? null : CardShadow();
        resources["Effect.Float"] = smooth ? null : new DropShadowEffect
        {
            BlurRadius = 40, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black
        };

        resources["Color.Accent"] = accent;
        resources["Radius.Card"] = new CornerRadius(appearance.CornerRadius);
        resources["Radius.Small"] = new CornerRadius(Math.Max(4, appearance.CornerRadius * 0.6));

        AcrylicHelper.SetDarkMode(System.Windows.Application.Current.MainWindow, dark);
    }

    private static DropShadowEffect CardShadow() => new()
    {
        BlurRadius = 24, ShadowDepth = 0, Opacity = 0.28, Color = Colors.Black
    };

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsLight(Color color)
        => (color.R * 0.299 + color.G * 0.587 + color.B * 0.114) > 186;

    public static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        text = text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            var converted = (Color)ColorConverter.ConvertFromString(text);
            return converted;
        }
        catch (FormatException) { return fallback; }
    }
}
