namespace LPML.Core.Instances;

/// <summary>
/// 实例文件夹：一个完整的游戏目录（内部结构与 .minecraft 一致，
/// 含 versions / libraries / assets / mods / saves 等），可以在里面直接下载安装游戏。
/// 路径既可以是绝对路径，也可以是相对启动器根目录的相对路径。
/// </summary>
public sealed class InstanceFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>目录路径：绝对（如 D:\Games\mc）或相对启动器根目录（如 games\survival）。</summary>
    public string Path { get; set; } = "";

    /// <summary>是否为默认文件夹（未指定时新实例落到这里）。</summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>兜底：万一某处 DisplayMemberPath 没生效，也别显示类名。</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>用户填的是相对还是绝对。</summary>
    public bool IsRelative => !string.IsNullOrWhiteSpace(Path)
                              && !System.IO.Path.IsPathRooted(System.IO.Path.GetFullPath(Path));
}
