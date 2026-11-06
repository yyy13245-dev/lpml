using LPML.Core.Config;
using LPML.Core.Utils;

namespace LPML.Core.Auth;

public enum AccountType
{
    Offline,
    Microsoft,
    Yggdrasil,
    LittleSkin
}

public sealed class Account : ObservableObject
{
    private string _userName = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AccountType Type { get; set; } = AccountType.Offline;
    public string UserName { get => _userName; set => Set(ref _userName, value); }
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? ClientToken { get; set; }

    /// <summary>
    /// MC 1.19+ 的 ${clientid} 占位符值（正版账户由微软/服务端下发）。
    /// 离线、外置账户留空即可。
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>MC 1.19+ 的 ${auth_xuid} 占位符值（正版账户的 Xbox 用户 ID）。</summary>
    public string? Xuid { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MinValue;

    /// <summary>外置登录 / authlib-injector 服务器地址（LittleSkin 用内置地址）。</summary>
    public string? AuthServer { get; set; }

    /// <summary>登录邮箱/用户名（外置登录用）。</summary>
    public string? LoginName { get; set; }

    public string? SkinUrl { get; set; }
    public string? CapeUrl { get; set; }
    public bool RequiresAuthlibInjector { get; set; }

    /// <summary>
    /// 这个账户单独存在哪里。每个账户可不同：同一个启动器里
    /// 可以既有便携账户，又有本机用户目录账户。
    /// </summary>
    public AccountStorageLocation StorageLocation { get; set; } = AccountStorageLocation.Portable;

    /// <summary>StorageLocation = Custom 时的目录；为空则回退到便携位置。</summary>
    public string? StorageDirectory { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;

    public bool IsMicrosoft => Type == AccountType.Microsoft;

    /// <summary>
    /// 是否可用于启动：离线只需有用户名；在线（微软 / 外置）还必须有访问令牌。
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(UserName) &&
        (Type == AccountType.Offline || !string.IsNullOrWhiteSpace(AccessToken));
    public bool IsOffline => Type == AccountType.Offline;
    public bool IsYggdrasil => Type is AccountType.Yggdrasil or AccountType.LittleSkin;

    public string TypeName => Type switch
    {
        AccountType.Microsoft => L.T("String.Account.TypeMicrosoft"),
        AccountType.Offline => L.T("String.Account.TypeOffline"),
        AccountType.LittleSkin => L.T("String.Account.TypeLittleSkin"),
        AccountType.Yggdrasil => L.T("String.Account.TypeExternal"),
        _ => L.T("String.Msg.Common.Unknown")
    };

    /// <summary>Mojang 用户类型：微软为 msa，其余为 legacy/mojang。</summary>
    public string UserType => Type switch
    {
        AccountType.Microsoft => "msa",
        _ => "legacy"
    };

    public bool IsExpired => ExpiresAt > DateTimeOffset.MinValue && ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(1);

    public string DisplayName => string.IsNullOrWhiteSpace(UserName) ? L.T("String.Core.Item.Unnamed") : UserName;

    public string ShortUuid => Uuid.Replace("-", "");

    public string StorageDisplayName => StorageLocation switch
    {
        AccountStorageLocation.UserProfile => L.T("String.Account.StorageUserProfile"),
        AccountStorageLocation.Custom => L.T("String.Msg.Account.LocationCustom"),
        _ => L.T("String.Msg.Account.LocationPortable")
    };

    public Account Clone() => (Account)MemberwiseClone();
}
