using LPML.Core.Config;
using LPML.Core.Download;
using LPML.Core.Utils;

namespace LPML.Core.Auth;

/// <summary>
/// 账号管理：每个账户可以单独存放在不同位置（便携 / 本机用户目录 / 已导入的自定义目录），
/// 启动时聚合所有位置下的 accounts.json，保存时按账户各自的归属写回。
/// </summary>
public sealed class AccountManager
{
    private readonly AppPaths _paths;
    private readonly MicrosoftAccountService _microsoft;
    private readonly YggdrasilService _yggdrasil;
    private readonly List<string> _importedDirectories = new();

    public List<Account> Accounts { get; private set; } = new();
    public string MicrosoftClientId { get; set; } = "";

    /// <summary>在线登录时自动打开浏览器。</summary>
    public bool AutoOpenBrowser { get; set; } = true;

    /// <summary>新账户的默认存放位置（不影响已有账户）。</summary>
    public AccountStorageLocation DefaultStorageLocation { get; set; } = AccountStorageLocation.Portable;

    /// <summary>新账户使用 Custom 位置时的默认目录。</summary>
    public string DefaultCustomDirectory { get; set; } = "";

    public event EventHandler? AccountsChanged;

    public AccountManager(AppPaths paths, HttpService http)
    {
        _paths = paths;
        _microsoft = new MicrosoftAccountService(http);
        _yggdrasil = new YggdrasilService(http);
    }

    // ── 位置解析 ────────────────────────────────────────────

    /// <summary>便携位置：&lt;root&gt;\LPML\accounts。</summary>
    public string PortableDirectory => _paths.ResolveAccountDirectory(AccountStorageLocation.Portable, null);

    /// <summary>本机用户目录：C:\Users\&lt;用户名&gt;\LPML\accounts。</summary>
    public string UserProfileDirectory => AppPaths.UserProfileAccountDirectory;

    /// <summary>所有参与聚合的目录（便携 + 本机用户目录 + 已导入的自定义目录）。</summary>
    public List<string> AllDirectories()
    {
        var list = new List<string> { PortableDirectory, UserProfileDirectory };
        foreach (var directory in _importedDirectories) list.Add(directory);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>这个账户实际应该写到哪个目录。</summary>
    public string DirectoryFor(Account account) => account.StorageLocation switch
    {
        AccountStorageLocation.UserProfile => UserProfileDirectory,
        // 自定义目录必须先手动导入，否则回退到便携位置
        AccountStorageLocation.Custom when !string.IsNullOrWhiteSpace(account.StorageDirectory)
                                           && _importedDirectories.Contains(account.StorageDirectory)
            => account.StorageDirectory,
        _ => PortableDirectory
    };

    /// <summary>设置已导入的自定义目录（从设置里读出来的，重启后依然生效）。</summary>
    public void SetImportedDirectories(IEnumerable<string>? directories)
    {
        _importedDirectories.Clear();
        if (directories is null) return;
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            if (!_importedDirectories.Contains(directory)) _importedDirectories.Add(directory);
        }
    }

    public IReadOnlyList<string> ImportedDirectories => _importedDirectories;

    /// <summary>手动导入一个自定义目录：只有导入后，里面的账户才会被加载、才能作为存放目标。</summary>
    public int ImportCustomDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return 0;

        var full = _paths.Expand(directory);
        if (!_importedDirectories.Contains(full)) _importedDirectories.Add(full);

        var loaded = Json.Load<List<Account>>(Path.Combine(full, AccountsFileName)) ?? new List<Account>();
        var count = 0;
        foreach (var account in loaded)
        {
            if (Accounts.Any(a => a.Id == account.Id)) continue;
            account.StorageLocation = AccountStorageLocation.Custom;
            account.StorageDirectory = full;
            Accounts.Add(account);
            count++;
        }
        if (count > 0) Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        Log.Info("Account", L.F("String.Core.Log.Account.ImportedDir", full, count));
        return count;
    }

    /// <summary>取消导入：里面的账户移回便携位置，不丢数据。</summary>
    public void RemoveImportedDirectory(string directory)
    {
        if (!_importedDirectories.Remove(directory)) return;
        foreach (var account in Accounts.Where(a =>
                     a.StorageLocation == AccountStorageLocation.Custom &&
                     string.Equals(a.StorageDirectory, directory, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            account.StorageLocation = AccountStorageLocation.Portable;
            account.StorageDirectory = null;
        }
        Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 读写 ────────────────────────────────────────────────

    public const string AccountsFileName = "accounts.json";

    /// <summary>聚合加载所有位置下的账户。</summary>
    public void Load()
    {
        var result = new List<Account>();
        foreach (var directory in AllDirectories())
        {
            var file = Path.Combine(directory, AccountsFileName);
            var list = Json.Load<List<Account>>(file);
            if (list is null) continue;

            foreach (var account in list)
            {
                if (result.Any(a => a.Id == account.Id)) continue;

                // 以所在目录为准回填归属，避免旧数据字段缺失
                if (string.Equals(directory, UserProfileDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    account.StorageLocation = AccountStorageLocation.UserProfile;
                    account.StorageDirectory = null;
                }
                else if (_importedDirectories.Contains(directory))
                {
                    account.StorageLocation = AccountStorageLocation.Custom;
                    account.StorageDirectory = directory;
                }
                else
                {
                    account.StorageLocation = AccountStorageLocation.Portable;
                    account.StorageDirectory = null;
                }

                result.Add(account);
            }
        }
        Accounts = result;
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按每个账户各自的归属写回各目录的 accounts.json。</summary>
    public void Save()
    {
        foreach (var directory in AllDirectories())
        {
            var mine = Accounts.Where(a =>
                string.Equals(DirectoryFor(a), directory, StringComparison.OrdinalIgnoreCase)).ToList();

            // 目录里原本没有任何账户、文件也不存在时，不凭空创建
            if (mine.Count == 0 && !File.Exists(Path.Combine(directory, AccountsFileName))) continue;

            try
            {
                Directory.CreateDirectory(directory);
                Json.Save(Path.Combine(directory, AccountsFileName), mine);
            }
            catch (IOException ex) { Log.Warn("Account", L.F("String.Core.Log.Account.WriteFailed", directory, ex.Message)); }
            catch (UnauthorizedAccessException ex) { Log.Warn("Account", L.F("String.Core.Log.Account.WriteFailed", directory, ex.Message)); }
        }
    }

    // ── 增删改 ──────────────────────────────────────────────

    public void Add(Account account)
    {
        ApplyDefaultStorage(account);
        Accounts.Add(account);
        Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(Account account)
    {
        Accounts.Remove(account);
        Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>给新账户套用默认存放位置。</summary>
    private void ApplyDefaultStorage(Account account)
    {
        account.StorageLocation = DefaultStorageLocation;
        account.StorageDirectory = DefaultStorageLocation == AccountStorageLocation.Custom
            ? DefaultCustomDirectory
            : null;

        // 默认自定义目录还没导入的话，先补进去，否则会静默回退到便携
        if (account.StorageLocation == AccountStorageLocation.Custom &&
            !string.IsNullOrWhiteSpace(account.StorageDirectory) &&
            !_importedDirectories.Contains(account.StorageDirectory!))
        {
            _importedDirectories.Add(account.StorageDirectory!);
        }
    }

    /// <summary>
    /// 单个账户改存放位置：先从旧位置移除，再写入新位置。
    /// 同一个启动器里因此可以并存便携账户与本机用户目录账户。
    /// </summary>
    public bool Move(Account account, AccountStorageLocation location, string? customDirectory = null)
    {
        if (account.StorageLocation == location &&
            string.Equals(account.StorageDirectory ?? "", customDirectory ?? "", StringComparison.OrdinalIgnoreCase))
            return false;

        if (location == AccountStorageLocation.Custom)
        {
            if (string.IsNullOrWhiteSpace(customDirectory)) return false;
            var full = _paths.Expand(customDirectory);
            if (!_importedDirectories.Contains(full)) _importedDirectories.Add(full);
            account.StorageDirectory = full;
        }
        else
        {
            account.StorageDirectory = null;
        }

        account.StorageLocation = location;
        Save();
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        Log.Info("Account", L.F("String.Core.Log.Account.LocationChanged", account.UserName, location));
        return true;
    }

    /// <summary>把所有账户统一迁到某个位置（可选，等价于批量 Move）。</summary>
    public int MoveAll(AccountStorageLocation location, string? customDirectory = null)
    {
        var count = 0;
        foreach (var account in Accounts.ToList())
            if (Move(account, location, customDirectory)) count++;
        return count;
    }

    public Account? Current(string? selectedId) =>
        Accounts.FirstOrDefault(a => a.Id == selectedId) ?? Accounts.FirstOrDefault();

    public Account CreateOffline(string playerName)
        => CreateOffline(playerName, null);

    /// <summary>
    /// 建离线账户。customUuid 为空时用官方规则生成；
    /// 填了则校验后使用（版本位与变体位会被强制修正，其余位保留用户输入）。
    /// </summary>
    public Account CreateOffline(string playerName, string? customUuid)
    {
        var name = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
        var uuid = string.IsNullOrWhiteSpace(customUuid)
            ? HashHelper.OfflineUuid(name)
            : OfflineUuidHelper.Normalize(customUuid);

        return new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.Offline,
            UserName = name,
            Uuid = uuid,
            AccessToken = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTimeOffset.MaxValue
        };
    }

    public Task<Account> LoginMicrosoftAsync(IProgress<string>? progress, CancellationToken token,
        Action<string, string>? onUserCode = null)
    {
        if (string.IsNullOrWhiteSpace(MicrosoftClientId))
            throw new InvalidOperationException(L.T("String.Msg.Account.NeedClientId"));
        return _microsoft.LoginAsync(MicrosoftClientId, progress, token, AutoOpenBrowser, onUserCode);
    }

    public Task<Account> LoginYggdrasilAsync(string serverUrl, string email, string password, AccountType type, CancellationToken token)
        => _yggdrasil.AuthenticateAsync(YggdrasilService.NormalizeServerUrl(serverUrl), email, password, type, token);

    /// <summary>启动时批量刷新即将过期的令牌。</summary>
    public async Task RefreshAllAsync(CancellationToken token = default)
    {
        var dirty = false;
        foreach (var account in Accounts.ToList())
        {
            if (account.IsOffline || !account.IsExpired) continue;
            try
            {
                switch (account.Type)
                {
                    case AccountType.Microsoft:
                        await _microsoft.RefreshAsync(account, MicrosoftClientId, token).ConfigureAwait(false);
                        dirty = true;
                        break;
                    case AccountType.Yggdrasil:
                    case AccountType.LittleSkin:
                        await _yggdrasil.RefreshAsync(account, token).ConfigureAwait(false);
                        dirty = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Account", L.F("String.Core.Log.Account.RefreshFailed", account.UserName, ex.Message));
            }
        }
        if (dirty) Save();
    }
}
