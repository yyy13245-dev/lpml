using LPML.Core.Utils;

namespace LPML.Core.Instances;

/// <summary>
/// 实例本地配置：存放在实例目录里的 lpml.json。
/// 跟着实例目录走，整个目录拷到别处（或拷给别人）时这些设置也一起过去。
/// 这里只放"脱离启动器注册表就没意义"的兜底信息，
/// 主要用于 .minecraft 被删、实例标识还在时也能恢复出可启动的实例。
/// </summary>
public sealed class InstanceLocalConfig
{
    /// <summary>配置文件名。</summary>
    public const string FileName = "lpml.json";

    /// <summary>实例名（注册表丢失时用它恢复显示名）。</summary>
    public string? Name { get; set; }

    /// <summary>实例 Id。</summary>
    public string? Id { get; set; }

    /// <summary>游戏版本。</summary>
    public string? VersionId { get; set; }

    /// <summary>加载器名与版本（Fabric / Forge 等）。</summary>
    public string? LoaderName { get; set; }
    public string? LoaderVersion { get; set; }

    /// <summary>隔离模式（随目录迁移时保持一致）。</summary>
    public IsolationMode? IsolationMode { get; set; }

    /// <summary>最近一次修改时间。</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>读取实例目录里的 lpml.json；不存在或损坏返回 null。</summary>
    public static InstanceLocalConfig? Load(string instanceDirectory)
    {
        try
        {
            var path = Path.Combine(instanceDirectory, FileName);
            return Json.Load<InstanceLocalConfig>(path);
        }
        catch (Exception) { return null; }
    }

    /// <summary>写入实例目录里的 lpml.json；失败静默（不该因为写配置让启动失败）。</summary>
    public static void Save(string instanceDirectory, InstanceLocalConfig config)
    {
        try
        {
            FileHelper.EnsureDirectory(instanceDirectory);
            config.UpdatedAt = DateTime.Now;
            Json.Save(Path.Combine(instanceDirectory, FileName), config);
        }
        catch (Exception) { }
    }

    /// <summary>用实例的当前状态生成本地配置。</summary>
    public static InstanceLocalConfig From(GameInstance instance, IsolationMode mode)
        => new()
        {
            Id = instance.Id,
            Name = instance.Name,
            VersionId = instance.VersionId,
            LoaderName = instance.LoaderName,
            LoaderVersion = instance.LoaderVersion,
            IsolationMode = mode
        };
}
