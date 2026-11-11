using System.Text.Json.Serialization;

namespace LPML.Core.Update;

/// <summary>
/// lpml.pages.dev/api/v1/lpml/update.json 的字段。
/// 键名是 snake_case，必须显式标注，PropertyNamingPolicy 不会把下划线去掉。
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>更新总顺序；启动器只认 num 严格更大的清单。</summary>
    [JsonPropertyName("num")] public int Num { get; set; }

    /// <summary>带 .NET 运行时的整包下载地址（两条线路）。</summary>
    [JsonPropertyName("have_runtime_down")] public List<string> HaveRuntimeDown { get; set; } = new();
    [JsonPropertyName("have_runtime_sha256")] public string HaveRuntimeSha256 { get; set; } = "";

    /// <summary>不带运行时的整包下载地址（两条线路）。自动模式下当前安装不带运行时时用它。</summary>
    [JsonPropertyName("no_runtime_down")] public List<string> NoRuntimeDown { get; set; } = new();
    [JsonPropertyName("no_runtime_sha256")] public string NoRuntimeSha256 { get; set; } = "";

    [JsonPropertyName("update_name_zh")] public string UpdateNameZh { get; set; } = "";
    [JsonPropertyName("update_name_en")] public string UpdateNameEn { get; set; } = "";
    [JsonPropertyName("update_introduce_zh")] public string UpdateIntroduceZh { get; set; } = "";
    [JsonPropertyName("update_introduce_en")] public string UpdateIntroduceEn { get; set; } = "";
    [JsonPropertyName("github")] public string Github { get; set; } = "";

    /// <summary>false 表示服务端暂停推送。</summary>
    [JsonPropertyName("have")] public bool Have { get; set; } = true;

    /// <summary>1 = 必须更新（弹窗不能关），2 = 重要更新，3 = 可选更新。</summary>
    [JsonPropertyName("important")] public int Important { get; set; } = 3;
}

/// <summary>一次检查的结果：原始清单字节、验签通过的签名文件名、本地化的更新说明正文。</summary>
public sealed class UpdateCheckResult
{
    public UpdateManifest Manifest { get; set; } = new();
    public byte[] Raw { get; set; } = Array.Empty<byte>();
    public string SignatureName { get; set; } = "";
    public string Introduce { get; set; } = "";

    public string DisplayName(bool chinese)
        => chinese
            ? (string.IsNullOrWhiteSpace(Manifest.UpdateNameZh) ? Manifest.Id : Manifest.UpdateNameZh)
            : (string.IsNullOrWhiteSpace(Manifest.UpdateNameEn) ? Manifest.Id : Manifest.UpdateNameEn);
}

/// <summary>
/// 写在 cache\update-*\lpml-update-stage.json 里的接力信息。
/// 命令行参数优先；这个文件是「按位置检测到自己是更新过程程序」的兜底。
/// </summary>
public sealed class UpdateStageInfo
{
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string Cleanup { get; set; } = "";
    public int Pid { get; set; }
    public int Num { get; set; }
    public string Id { get; set; } = "";
    public string Exe { get; set; } = "LPML.exe";
}

/// <summary>一次成功的下载 + 解压：暂存目录、新程序目录、可执行文件名。</summary>
public sealed class UpdateStagePackage
{
    public string StageDirectory { get; set; } = "";
    public string SourceDirectory { get; set; } = "";
    public string ExecutableName { get; set; } = "LPML.exe";
}

public sealed class UpdateException : Exception
{
    public UpdateException(string message) : base(message) { }
    public UpdateException(string message, Exception inner) : base(message, inner) { }
}
