using LPML.Core.Config;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LPML.App;

/// <summary>
/// 统一动效入口：页面切换（淡入 + 上浮 + 轻微拉伸）、元素入场、拉伸反馈。
/// 可通过"外观 → 关闭动效"一键静止，照顾低性能设备。
/// </summary>
public static class AnimationHelper
{
    /// <summary>true 表示流畅模式：只保留最轻量的动画。</summary>
    public static bool Smooth => App.Context?.Settings?.Appearance?.PerformanceMode == PerformanceMode.Smooth;

    public static Duration Duration(int? milliseconds = null)
    {
        var settings = App.Context?.Settings?.Appearance;
        var ms = milliseconds ?? settings?.AnimationDurationMs ?? 260;
        if (settings is { EnableAnimations: false }) ms = 1;
        // 流畅模式：动画本身保留（否则界面切换太生硬），但时长压到很短
        if (Smooth) ms = Math.Min(ms, 90);
        return new Duration(TimeSpan.FromMilliseconds(ms));
    }

    public static bool Enabled
    {
        get
        {
            return App.Context?.Settings?.Appearance?.EnableAnimations ?? true;
        }
    }

    /// <summary>页面切换：从下方淡入并轻微放大。</summary>
    public static void PlayPageEnter(FrameworkElement element)
    {
        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }

        // 流畅模式：只做淡入。位移和缩放都要额外的 Transform 合成，是最贵的两条
        if (Smooth)
        {
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Duration(90)));
            return;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var group = new TransformGroup();
        var translate = new TranslateTransform(0, 18);
        var scale = new ScaleTransform(0.985, 0.985);
        group.Children.Add(scale);
        group.Children.Add(translate);
        element.RenderTransform = group;

        var storyboard = new Storyboard();
        storyboard.Children.Add(new DoubleAnimation
        {
            From = 0, To = 1, Duration = Duration(),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Storyboard.SetTarget(storyboard.Children[0], element);
        Storyboard.SetTargetProperty(storyboard.Children[0], new PropertyPath(UIElement.OpacityProperty));

        storyboard.Children.Add(new DoubleAnimation
        {
            From = 18, To = 0, Duration = Duration(320),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Storyboard.SetTarget(storyboard.Children[1], element);
        Storyboard.SetTargetProperty(storyboard.Children[1], new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)"));

        storyboard.Children.Add(new DoubleAnimation
        {
            From = 0.985, To = 1, Duration = Duration(320),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Storyboard.SetTarget(storyboard.Children[2], element);
        Storyboard.SetTargetProperty(storyboard.Children[2], new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)"));

        storyboard.Children.Add(new DoubleAnimation
        {
            From = 0.985, To = 1, Duration = Duration(320),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Storyboard.SetTarget(storyboard.Children[3], element);
        Storyboard.SetTargetProperty(storyboard.Children[3], new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleY)"));

        storyboard.Begin();
    }

    /// <summary>元素逐个入场（列表 / 卡片）。</summary>
    public static void StaggeredEntrance(IEnumerable<FrameworkElement> elements, double delayStep = 0.04)
    {
        // 流畅模式直接跳过：每项一个 Storyboard + Transform，列表长时开销明显
        if (!Enabled || Smooth) return;
        var index = 0;
        foreach (var element in elements)
        {
            element.Opacity = 0;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(0.96, 0.96));
            group.Children.Add(new TranslateTransform(0, 12));
            element.RenderTransform = group;

            var begin = TimeSpan.FromSeconds(index * delayStep);
            var storyboard = new Storyboard { BeginTime = begin };
            storyboard.Children.Add(new DoubleAnimation(0, 1, Duration(300)));
            Storyboard.SetTarget(storyboard.Children[0], element);
            Storyboard.SetTargetProperty(storyboard.Children[0], new PropertyPath(UIElement.OpacityProperty));

            storyboard.Children.Add(new DoubleAnimation(0.96, 1, Duration(340)));
            Storyboard.SetTarget(storyboard.Children[1], element);
            Storyboard.SetTargetProperty(storyboard.Children[1], new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)"));

            storyboard.Children.Add(new DoubleAnimation(0.96, 1, Duration(340)));
            Storyboard.SetTarget(storyboard.Children[2], element);
            Storyboard.SetTargetProperty(storyboard.Children[2], new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleY)"));

            storyboard.Children.Add(new DoubleAnimation(12, 0, Duration(340)));
            Storyboard.SetTarget(storyboard.Children[3], element);
            Storyboard.SetTargetProperty(storyboard.Children[3], new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)"));

            storyboard.Begin();
            index++;
        }
    }

    /// <summary>拉伸反馈（点击按钮 / 卡片时的横向拉伸）。</summary>
    public static void Stretch(FrameworkElement element, double amount = 0.04)
    {
        if (!Enabled) return;
        // 流畅模式：两个状态直接切换，不补间
        if (Smooth)
        {
            var snap = new ScaleTransform(1 + amount, 1 - amount * 0.6);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = snap;
            return;
        }
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = element.RenderTransform as ScaleTransform;
        if (scale is null)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
        }
        var duration = Duration(140);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1 + amount, duration));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1 - amount * 0.6, duration));
    }

    public static void Release(FrameworkElement element)
    {
        if (!Enabled) return;
        if (Smooth)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = new ScaleTransform(1, 1);
            return;
        }
        if (element.RenderTransform is ScaleTransform scale)
        {
            var duration = Duration(220);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    public static void FadeIn(FrameworkElement element, int milliseconds = 240)
    {
        if (!Enabled) { element.Opacity = 1; return; }
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, Duration(milliseconds)));
    }
}
