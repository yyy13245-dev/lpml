using System.Text.Json;
using System.Text.Json.Serialization;

namespace LPML.Core.Utils;

/// <summary>
/// 全局 JSON 配置：宽松解析（容忍尾逗号、注释、大小写不敏感），
/// 保证第三方（Mojang / BMCLAPI / Fabric / Modrinth）返回的 JSON 结构变化不会直接崩溃。
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static T? Deserialize<T>(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        try { return JsonSerializer.Deserialize<T>(text, Options); }
        catch (JsonException) { return default; }
    }

    public static T? Deserialize<T>(Stream stream)
    {
        try { return JsonSerializer.Deserialize<T>(stream, Options); }
        catch (JsonException) { return default; }
    }

    public static string Serialize<T>(T value, bool indented = true)
        => JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(value));
    }

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Deserialize<T>(File.ReadAllText(path));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
