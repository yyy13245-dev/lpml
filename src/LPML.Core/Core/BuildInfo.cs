using System;
using System.Reflection;

namespace LPML.Core;

/// <summary>
/// 软件版本号的唯一来源：读取 csproj 的 &lt;Version&gt;（程序集信息版本）。
/// 代码里不要再写版本号字面量，改版本只改两个 csproj 即可。
/// </summary>
public static class BuildInfo
{
    private static readonly Lazy<string> Cached = new(Read);

    /// <summary>形如 0.0.002-1；预发布后缀保留，仅去掉 SourceLink 附带的 +commit 后缀。</summary>
    public static string Version => Cached.Value;

    private static string Read()
    {
        try
        {
            var info = typeof(BuildInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
            return typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        }
        catch { return "unknown"; }
    }
}
