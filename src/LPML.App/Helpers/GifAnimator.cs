using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LPML.App.Helpers;

/// <summary>
/// GIF 动图播放器。
/// WPF 的 BitmapImage 加载 GIF 只会显示第一帧，所以这里自己解码逐帧并按延迟切换。
/// </summary>
public sealed class GifAnimator
{
    private readonly Image _target;
    private readonly DispatcherTimer _timer;
    private List<GifFrame> _frames = new();
    private int _index;

    public GifAnimator(Image target)
    {
        _target = target;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
    }

    private sealed class GifFrame
    {
        public BitmapSource Source { get; set; } = null!;
        public int DelayMs { get; set; }
    }

    /// <summary>加载并播放；返回是否真的是动图（帧数 &gt; 1）。</summary>
    public bool Play(string path)
    {
        Stop();
        try
        {
            var decoder = new GifBitmapDecoder(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            _frames = new List<GifFrame>();
            foreach (var frame in decoder.Frames)
            {
                // 元数据里取每帧延迟；取不到就按 100ms
                var delay = 100;
                try
                {
                    if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("/grctlext/Delay") is ushort value)
                        delay = value == 0 ? 100 : value * 10;
                }
                catch (Exception) { /* 元数据缺失用默认值 */ }

                _frames.Add(new GifFrame { Source = frame, DelayMs = delay });
            }

            if (_frames.Count == 0) return false;

            if (_frames.Count == 1)
            {
                // 静态 GIF：直接显示，不启定时器
                _target.Source = _frames[0].Source;
                return false;
            }

            _index = 0;
            _target.Source = _frames[0].Source;
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(_frames[0].DelayMs, 20));
            _timer.Start();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_frames.Count == 0) { Stop(); return; }
        _index = (_index + 1) % _frames.Count;
        var frame = _frames[_index];
        _target.Source = frame.Source;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(frame.DelayMs, 20));
    }

    public void Stop()
    {
        _timer.Stop();
        _frames.Clear();
        _index = 0;
    }
}
