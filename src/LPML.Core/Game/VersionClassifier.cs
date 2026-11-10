using System.Globalization;
using System.Text.RegularExpressions;
using LPML.Core.Game.Models;

namespace LPML.Core.Game;

/// <summary>
/// 官方 version_manifest_v2.json 只有 release / snapshot / old_alpha / old_beta 四种 type，
/// 愚人节构建在清单里一律标成 snapshot，没有独立字段。这里按「已知版本 ID +
/// 发布时间窗口 + 常规命名规则」把它们单独分出来，供下载中心筛选。
/// </summary>
public enum VersionChannel
{
    Release,
    Snapshot,
    AprilFools,
    Legacy
}

public static class VersionClassifier
{
    /// <summary>
    /// 官方清单中确实存在的愚人节构建。26w14a 的 ID 与常规快照同形，
    /// 无法靠命名或日期区分，只能靠白名单识别；带特殊后缀的新年份会由日期规则自动收进来。
    /// </summary>
    private static readonly HashSet<string> KnownAprilFools = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a",
        "1.RV-Pre1",
        "3D Shareware v1.34",
        "20w14infinite",
        "22w13oneblockatatime",
        "23w13a_or_b",
        "24w14potato",
        "25w14craftmine",
        "26w14a"
    };

    /// <summary>
    /// 常规快照、预发布版、候选版的 ID 形状；命中这些就不靠日期判成愚人节。
    /// 版本号允许多段（26.1.1-rc-1 / 1.21.11-rc3），否则 3 月底发布的 rc 会被误判。
    /// </summary>
    private static readonly Regex NormalSnapshotId = new(
        @"^(?:\d{2}w\d{2}[a-z]|\d+(?:\.\d+)+(?:-snapshot-\d+|-(?:pre|rc)-?\d+))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static VersionChannel Classify(ManifestVersion version)
    {
        var type = version.Type ?? "";
        var id = version.Id ?? "";

        if (string.Equals(type, "release", StringComparison.OrdinalIgnoreCase))
            return VersionChannel.Release;
        if (KnownAprilFools.Contains(id))
            return VersionChannel.AprilFools;
        if (!string.Equals(type, "snapshot", StringComparison.OrdinalIgnoreCase))
            return VersionChannel.Legacy;

        // 3 月 30 日～4 月 3 日之间发布的非标准命名快照，视为愚人节构建。
        // 常规快照（25w14a）和 rc（26.1.1-rc-1）也落在这个窗口，靠命名规则排除。
        if (IsAprilFoolsWindow(version.ReleaseTime) && !NormalSnapshotId.IsMatch(id))
            return VersionChannel.AprilFools;

        return VersionChannel.Snapshot;
    }

    private static bool IsAprilFoolsWindow(string? releaseTime)
    {
        if (string.IsNullOrWhiteSpace(releaseTime)) return false;
        if (!DateTimeOffset.TryParse(releaseTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var time)) return false;
        return (time.Month == 3 && time.Day >= 30) || (time.Month == 4 && time.Day <= 3);
    }

    public static string ChannelKey(VersionChannel channel) => channel switch
    {
        VersionChannel.Release => "String.Download.Type.Release",
        VersionChannel.Snapshot => "String.Download.Type.Snapshot",
        VersionChannel.AprilFools => "String.Download.Type.AprilFools",
        _ => "String.Download.Type.Legacy"
    };

    /// <summary>第三方镜像偶尔只同步近期版本；用它判断是否需要补一份官方清单。</summary>
    public static bool HasAnyAprilFools(IEnumerable<ManifestVersion> versions)
        => versions.Any(v => KnownAprilFools.Contains(v.Id ?? ""));
}
