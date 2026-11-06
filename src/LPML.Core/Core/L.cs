using System;

namespace LPML.Core;

/// <summary>
/// Core 层的本地化桥接。Core 是纯逻辑库，不认识 WPF 资源，
/// 因此由宿主（LPML.App）在启动时注入 Provider；未注入时原样返回键名。
/// </summary>
public static class L
{
    /// <summary>键 → 文本的实现，由 App 层注入（通常是 Lang.T）。</summary>
    public static Func<string, string> Provider { get; set; } = key => key;

    public static string T(string key) => Provider(key);

    /// <summary>带 {0} {1} 占位符的词条。</summary>
    public static string F(string key, params object[] args)
    {
        var text = T(key);
        try { return string.Format(text, args); }
        catch (FormatException) { return text; }
    }
}
