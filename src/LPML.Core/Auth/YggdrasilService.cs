using System.Net.Http.Json;
using LPML.Core.Download;
using LPML.Core.Utils;

namespace LPML.Core.Auth;

/// <summary>
/// Yggdrasil 登录：LittleSkin、以及任意自建外置登录（authlib-injector）服务器。
/// </summary>
public sealed class YggdrasilService
{
    private readonly HttpService _http;

    public YggdrasilService(HttpService http)
    {
        _http = http;
    }

    public const string LittleSkinServer = "https://littleskin.cn/api/yggdrasil";

    public static string NormalizeServerUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return LittleSkinServer;
        var trimmed = url.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)) trimmed = "https://" + trimmed;
        if (!trimmed.EndsWith("/api/yggdrasil", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.TrimEnd('/') + "/api/yggdrasil";
        }
        return trimmed;
    }

    public async Task<ServerMeta?> GetServerMetaAsync(string serverUrl, CancellationToken token = default)
    {
        try
        {
            var text = await _http.GetStringAsync(serverUrl + "/", token).ConfigureAwait(false);
            return Json.Deserialize<ServerMeta>(text);
        }
        catch
        {
            return null;
        }
    }

    public async Task<Account> AuthenticateAsync(string serverUrl, string email, string password,
        AccountType type, CancellationToken token = default)
    {
        var client = CreateClient();
        var clientToken = Guid.NewGuid().ToString("N");
        var payload = new
        {
            username = email,
            password,
            clientToken,
            requestUser = true,
            agent = new { name = "Minecraft", version = 1 }
        };

        using var response = await client.PostAsJsonAsync(serverUrl + "/authserver/authenticate", payload, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            var error = Json.Deserialize<YggdrasilError>(text);
            throw new IOException(error?.ErrorMessage ?? L.F("String.Core.Err.Yggdrasil.LoginFailed", (int)response.StatusCode));
        }

        var result = Json.Deserialize<AuthenticateResponse>(text) ?? throw new IOException(L.T("String.Core.Err.Yggdrasil.ParseFailed"));
        var profile = result.SelectedProfile ?? result.AvailableProfiles?.FirstOrDefault();
        if (profile is null) throw new IOException(L.T("String.Core.Err.Yggdrasil.NoProfile"));

        return new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            UserName = profile.Name ?? "",
            Uuid = HashHelper.FormatUuid(profile.Id ?? ""),
            AccessToken = result.AccessToken ?? "",
            ClientToken = result.ClientToken ?? clientToken,
            AuthServer = serverUrl,
            LoginName = email,
            RequiresAuthlibInjector = true,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
        };
    }

    public async Task RefreshAsync(Account account, CancellationToken token = default)
    {
        var serverUrl = account.AuthServer;
        if (string.IsNullOrWhiteSpace(serverUrl)) throw new IOException(L.T("String.Core.Err.Yggdrasil.NoServer"));
        var client = CreateClient();
        var payload = new
        {
            accessToken = account.AccessToken,
            clientToken = account.ClientToken,
            requestUser = true,
            selectedProfile = new { id = account.ShortUuid, name = account.UserName }
        };
        using var response = await client.PostAsJsonAsync(serverUrl + "/authserver/refresh", payload, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new IOException(L.T("String.Core.Err.Yggdrasil.RefreshFailed"));
        var result = Json.Deserialize<AuthenticateResponse>(text);
        if (result?.AccessToken is null) throw new IOException(L.T("String.Core.Err.Yggdrasil.RefreshMalformed"));
        account.AccessToken = result.AccessToken;
        account.ClientToken = result.ClientToken ?? account.ClientToken;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
    }

    public async Task<bool> ValidateAsync(Account account, CancellationToken token = default)
    {
        try
        {
            var client = CreateClient();
            var payload = new { accessToken = account.AccessToken, clientToken = account.ClientToken };
            using var response = await client.PostAsJsonAsync(account.AuthServer + "/authserver/validate", payload, token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>生成 authlib-injector 的 prefetched 注入参数。</summary>
    public static string BuildPrefetched(Account account)
    {
        var payload = new
        {
            accessToken = account.AccessToken,
            clientToken = account.ClientToken ?? "",
            selectedProfile = new { id = account.ShortUuid, name = account.UserName },
            user = new { id = account.ShortUuid, username = account.LoginName ?? account.UserName }
        };
        var json = Json.Serialize(payload, false);
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public sealed class ServerMeta
    {
        public ServerMetaInfo? Meta { get; set; }
        public string? SkinDomains { get; set; }
        public string? SignaturePublickey { get; set; }
    }

    public sealed class ServerMetaInfo
    {
        public string? ServerName { get; set; }
        public string? ImplementationName { get; set; }
        public string? ImplementationVersion { get; set; }
        public Dictionary<string, string>? Links { get; set; }
    }

    private sealed class AuthenticateResponse
    {
        public string? AccessToken { get; set; }
        public string? ClientToken { get; set; }
        public YggdrasilProfile? SelectedProfile { get; set; }
        public List<YggdrasilProfile>? AvailableProfiles { get; set; }
    }

    public sealed class YggdrasilProfile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class YggdrasilError
    {
        public string? Error { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
