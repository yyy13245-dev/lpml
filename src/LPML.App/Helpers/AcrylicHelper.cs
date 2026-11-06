using LPML.Core.Config;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LPML.App;

/// <summary>
/// 毛玻璃 / Mica / Acrylic 支持：
/// Windows 11 使用 DWMWA_SYSTEMBACKDROP_TYPE；Windows 10 回退到 SetWindowCompositionAttribute。
/// 失败时界面自动退化为半透明背景 + 背景图模糊，不影响使用。
/// </summary>
public static class AcrylicHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaTransitionsForcedisabled = 3;

    public static bool Enable(Window window, BackdropMode mode)
    {
        if (mode == BackdropMode.None) return false;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            int backdrop = mode switch
            {
                BackdropMode.Mica => 2,          // DWMSBT_MAINWINDOW
                BackdropMode.Acrylic => 3,       // DWMSBT_TRANSIENTWINDOW
                BackdropMode.BlurBehind => 4,    // DWMSBT_TABBEDWINDOW
                _ => 0
            };

            var value = backdrop;
            var result = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref value, sizeof(int));
            if (result != 0)
                return EnableLegacyAcrylic(handle);

            var corner = 2; // 圆角
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (ArgumentException) { return false; }
        catch (Exception) { return false; }
    }

    /// <summary>关掉系统背景（流畅模式用）：把 DWM 背景类型设回"自动/无"。</summary>
    public static void Disable(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            // 1 = DWMSBT_NONE（Windows 11）；0 = DWMSBT_AUTO，旧系统会当成无背景
            foreach (var value in new[] { 1, 0 })
            {
                var v = value;
                if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref v, sizeof(int)) == 0)
                    break;
            }
        }
        catch (Exception) { /* 装饰性能力，失败无所谓 */ }
    }

    private static bool EnableLegacyAcrylic(IntPtr handle)
    {
        try
        {
            var accent = new AccentPolicy { AccentState = 4, GradientColor = unchecked((int)0x99000000) };
            var data = new WindowCompositionAttributeData
            {
                Attribute = 19,
                Data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>()),
                SizeOfData = Marshal.SizeOf<AccentPolicy>()
            };
            Marshal.StructureToPtr(accent, data.Data, false);
            SetWindowCompositionAttribute(handle, ref data);
            Marshal.FreeHGlobal(data.Data);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (ArgumentException) { return false; }
        catch (Exception) { return false; }
    }

    public static void SetDarkMode(Window? window, bool dark)
    {
        if (window is null) return;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (ArgumentException) { }
        catch (Exception) { }
    }

    public static void DisableTransitions(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var value = 1;
            DwmSetWindowAttribute(handle, DwmwaTransitionsForcedisabled, ref value, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (ArgumentException) { }
        catch (Exception) { }
    }

    // PreserveSig 必须为 true：若为 false，CLR 会把 DWM 返回的失败 HRESULT
    // （如 E_INVALIDARG）自动转成 ArgumentException("Value does not fall within the expected range.")，
    // 毛玻璃只是装饰性功能，不该让整个启动器崩掉。
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }
}
