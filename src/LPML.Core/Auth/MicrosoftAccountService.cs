using System.Net.Http.Json;
using LPML.Core.Download;
using LPML.Core.Utils;

namespace LPML.Core.Auth;

/// <summary>
/// 微软登录（OAuth 2.0 设备代码流 → XBL → XSTS → Minecraft Services）。
/// client_id 由用户在设置中填写自己注册的应用（遵守微软条款）。
/// </summary>
public sealed class MicrosoftAccountService
{
    private readonly HttpService _http;

    public MicrosoftAccountService(HttpService http)
    {
        _http = http;
    }

    /// <summary>第一步：请求设备代码，返回给用户的验证码与验证网址。</summary>
    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(string clientId, CancellationToken token = default)
    {
        var client = CreateClient();
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "XboxLive.signin offline_access"
        });
        using var response = await client.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", body, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new IOException(L.F("String.Ms.DeviceCodeFailed", text));
        return Json.Deserialize<DeviceCodeResponse>(text) ?? throw new IOException(L.T("String.Ms.DeviceCodeParseFailed"));
    }

    /// <summary>第二步：轮询用户授权，直到拿到微软令牌。</summary>
    public async Task<MicrosoftTokenResponse> PollAsync(string clientId, DeviceCodeResponse deviceCode,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var client = CreateClient();
        var interval = TimeSpan.FromSeconds(Math.Max(3, deviceCode.Interval));
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, deviceCode.ExpiresIn));
        progress?.Report(L.T("String.Ms.WaitingAuth"));

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(interval, token).ConfigureAwait(false);
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = deviceCode.DeviceCode ?? ""
            });
            using var response = await client.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", body, token);
            var text = await response.Content.ReadAsStringAsync(token);
            var result = Json.Deserialize<MicrosoftTokenResponse>(text);
            if (response.IsSuccessStatusCode && result?.AccessToken is not null) return result;
            if (result?.Error is null) throw new IOException(L.F("String.Ms.TokenParseFailed", text));
            switch (result.Error)
            {
                case "authorization_pending":
                    progress?.Report(L.T("String.Ms.NotYetAuthorized"));
                    continue;
                case "authorization_declined":
                    throw new IOException(L.T("String.Ms.Denied"));
                case "expired_token":
                    throw new IOException(L.T("String.Ms.Expired"));
                case "slow_down":
                    interval = interval + TimeSpan.FromSeconds(5);
                    continue;
                default:
                    throw new IOException(L.F("String.Ms.LoginFailed", result.ErrorDescription));
            }
        }
        throw new TimeoutException(L.T("String.Ms.Timeout"));
    }

    /// <summary>完整登录：微软令牌 → XBL → XSTS → Minecraft → 角色信息。</summary>
    /// <summary>在线账户认证：拉设备代码 → 跳浏览器 → 轮询令牌 → XBL / XSTS / Minecraft。</summary>
    /// <param name="openBrowser">是否在拿到验证码后自动用默认浏览器打开认证页（默认 true）。</param>
    public async Task<Account> LoginAsync(string clientId, IProgress<string>? progress = null,
        CancellationToken token = default, bool openBrowser = true,
        Action<string, string>? onUserCode = null)
    {
        var device = await RequestDeviceCodeAsync(clientId, token).ConfigureAwait(false);

        var uri = string.IsNullOrWhiteSpace(device.VerificationUri)
            ? "https://www.microsoft.com/link"
            : device.VerificationUri;

        if (openBrowser && TryOpenBrowser(uri))
            progress?.Report(L.F("String.Ms.BrowserOpened", device.UserCode, uri));
        else
            progress?.Report(L.F("String.Ms.OpenManually", uri, device.UserCode));

        // 结构化地把验证码交给调用方，避免 UI 去解析本地化后的进度文本
        onUserCode?.Invoke(device.UserCode ?? "", uri);

        var msToken = await PollAsync(clientId, device, progress, token).ConfigureAwait(false);
        progress?.Report(L.T("String.Ms.AuthenticatingXbl"));
        var xbl = await AuthenticateXboxLiveAsync(msToken.AccessToken!, token).ConfigureAwait(false);
        progress?.Report(L.T("String.Ms.FetchingXsts"));
        var xsts = await AuthorizeXstsAsync(xbl.Token!, token).ConfigureAwait(false);
        progress?.Report(L.T("String.Ms.LoggingIntoMc"));
        var mc = await LoginMinecraftAsync(xsts.UserHash!, xsts.Token!, token).ConfigureAwait(false);
        progress?.Report(L.T("String.Ms.ReadingProfile"));
        var profile = await GetProfileAsync(mc.AccessToken!, token).ConfigureAwait(false);

        if (profile is null || string.IsNullOrEmpty(profile.Name))
            throw new IOException(L.T("String.Ms.NoGameOrProfile"));

        return new Account
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = AccountType.Microsoft,
            UserName = profile.Name!,
            Uuid = HashHelper.FormatUuid(profile.Id ?? ""),
            AccessToken = mc.AccessToken ?? "",
            RefreshToken = msToken.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(mc.ExpiresIn > 0 ? mc.ExpiresIn : 86400),
            SkinUrl = profile.Skins?.FirstOrDefault(s => s.State == "ACTIVE")?.Url
                     ?? profile.Skins?.FirstOrDefault()?.Url,
            CapeUrl = profile.Capes?.FirstOrDefault(c => c.State == "ACTIVE")?.Url
        };
    }

    /// <summary>用系统默认浏览器打开认证页；失败时静默降级为「手动复制代码」。</summary>
    private static bool TryOpenBrowser(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>用 refresh_token 静默续期。</summary>
    public async Task RefreshAsync(Account account, string clientId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken)) throw new IOException(L.T("String.Ms.NoRefreshToken"));
        var client = CreateClient();
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = account.RefreshToken!,
            ["scope"] = "XboxLive.signin offline_access"
        });
        using var response = await client.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", body, token);
        var text = await response.Content.ReadAsStringAsync(token);
        var ms = Json.Deserialize<MicrosoftTokenResponse>(text);
        if (!response.IsSuccessStatusCode || ms?.AccessToken is null) throw new IOException(L.T("String.Ms.RefreshFailed"));

        var xbl = await AuthenticateXboxLiveAsync(ms.AccessToken!, token).ConfigureAwait(false);
        var xsts = await AuthorizeXstsAsync(xbl.Token!, token).ConfigureAwait(false);
        var mc = await LoginMinecraftAsync(xsts.UserHash!, xsts.Token!, token).ConfigureAwait(false);
        account.AccessToken = mc.AccessToken ?? "";
        account.RefreshToken = ms.RefreshToken ?? account.RefreshToken;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(mc.ExpiresIn > 0 ? mc.ExpiresIn : 86400);
    }

    private async Task<(string? Token, string? UserHash)> AuthenticateXboxLiveAsync(string msToken, CancellationToken token)
    {
        var client = CreateClient();
        var payload = new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + msToken },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        using var response = await client.PostAsJsonAsync("https://user.auth.xboxlive.com/user/authenticate", payload, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new IOException(L.F("String.Ms.XblFailed", text));
        var result = Json.Deserialize<XboxResponse>(text) ?? throw new IOException(L.T("String.Ms.XblParseFailed"));
        var uhs = result.DisplayClaims?.Xui?.FirstOrDefault()?.Uhs;
        return (result.Token, uhs);
    }

    private async Task<(string? Token, string? UserHash)> AuthorizeXstsAsync(string xblToken, CancellationToken token)
    {
        var client = CreateClient();
        var payload = new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            // 现行 Minecraft Services 依赖方（Minecraft Wiki / wiki.vg）。
            // 旧的 http://minecraft.api.microsoft.com 已不再推荐。
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };
        using var response = await client.PostAsJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", payload, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
        {
            if (text.Contains("2148916233")) throw new IOException(L.T("String.Ms.NoXboxProfile"));
            if (text.Contains("2148916238")) throw new IOException(L.T("String.Ms.NeedParentConsent"));
            throw new IOException(L.F("String.Ms.XstsFailed", text));
        }
        var result = Json.Deserialize<XboxResponse>(text) ?? throw new IOException(L.T("String.Ms.XstsParseFailed"));
        return (result.Token, result.DisplayClaims?.Xui?.FirstOrDefault()?.Uhs);
    }

    private async Task<MinecraftLoginResponse> LoginMinecraftAsync(string userHash, string xstsToken, CancellationToken token)
    {
        var client = CreateClient();
        var payload = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };
        using var response = await client.PostAsJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", payload, token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new IOException(L.F("String.Ms.McLoginFailed", text));
        return Json.Deserialize<MinecraftLoginResponse>(text) ?? throw new IOException(L.T("String.Ms.McParseFailed"));
    }

    public async Task<MinecraftProfile?> GetProfileAsync(string accessToken, CancellationToken token = default)
    {
        var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) return null;
        return Json.Deserialize<MinecraftProfile>(text);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    // ── 响应模型 ────────────────────────────────────────────
    public sealed class DeviceCodeResponse
    {
        public string? UserCode { get; set; }
        public string? DeviceCode { get; set; }
        public string? VerificationUri { get; set; }
        public int ExpiresIn { get; set; } = 900;
        public int Interval { get; set; } = 5;
        public string? Message { get; set; }
    }

    public sealed class MicrosoftTokenResponse
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public string? TokenType { get; set; }
        public int ExpiresIn { get; set; }
        public string? Error { get; set; }
        public string? ErrorDescription { get; set; }
    }

    public sealed class XboxResponse
    {
        public string? Token { get; set; }
        public XboxDisplayClaims? DisplayClaims { get; set; }
    }

    public sealed class XboxDisplayClaims
    {
        public List<XboxUser>? Xui { get; set; }
    }

    public sealed class XboxUser
    {
        public string? Uhs { get; set; }
    }

    public sealed class MinecraftLoginResponse
    {
        public string? AccessToken { get; set; }
        public string? Username { get; set; }
        public int ExpiresIn { get; set; } = 86400;
    }

    public sealed class MinecraftProfile
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<ProfileTexture>? Skins { get; set; }
        public List<ProfileTexture>? Capes { get; set; }
    }

    public sealed class ProfileTexture
    {
        public string? Id { get; set; }
        public string? State { get; set; }
        public string? Url { get; set; }
    }
}
