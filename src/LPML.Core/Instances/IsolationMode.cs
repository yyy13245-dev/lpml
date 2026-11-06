namespace LPML.Core.Instances;

/// <summary>
/// 实例隔离模式。
/// 用户数据隔离：只隔离 mods / saves / config 这类会变的用户数据，
///              版本文件（versions / libraries / assets）仍在 .minecraft，省空间。
/// 完整隔离：版本文件也放进实例目录，整个实例目录拷走就能迁移，代价是重复占空间。
/// </summary>
public enum IsolationMode
{
    /// <summary>仅用户数据隔离（省空间）。</summary>
    UserData = 0,

    /// <summary>完整隔离（版本文件也独立，方便迁移）。</summary>
    Full = 1
}
