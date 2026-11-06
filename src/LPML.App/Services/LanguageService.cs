using LPML.Core.Config;
using System.Collections.Generic;
using System.Windows;

namespace LPML.App.Services;

/// <summary>
/// 语言服务：运行时切换界面语言。
/// 词条放在 Locales\*.xaml（ResourceDictionary），XAML 侧用 DynamicResource 引用，
/// 因此切换语言时界面无需重建即可刷新。
/// </summary>
public sealed class LanguageService
{
    /// <summary>当前挂上去的语言包，切换时先摘掉再插入新的。</summary>
    private ResourceDictionary? _current;

    private readonly Dictionary<string, ResourceDictionary> _cache = new();

    /// <summary>当前语言代码，如 zh-CN / en-US。</summary>
    public string CurrentCode { get; private set; } = "zh-CN";

    /// <summary>语言切换后触发（需要手动刷新非绑定文本的页面可订阅）。</summary>
    public event System.EventHandler? LanguageChanged;

    private static string ToCode(LauncherLanguage language) => language switch
    {
        LauncherLanguage.English => "en-US",
        _ => "zh-CN"
    };

    /// <summary>应用指定语言到应用程序资源。</summary>
    public void Apply(LauncherLanguage language)
    {
        var code = ToCode(language);
        var dictionaries = Application.Current.Resources.MergedDictionaries;

        var next = LoadDictionary(code);
        if (ReferenceEquals(next, _current)) return;

        // 词条放在最前面（String.* 与主题的 Brush.* 不冲突）；
        // 换掉字典引用后，所有 {DynamicResource String.xxx} 会自动重新求值
        if (_current is not null) dictionaries.Remove(_current);
        dictionaries.Insert(0, next);
        _current = next;

        CurrentCode = code;
        LanguageChanged?.Invoke(this, System.EventArgs.Empty);
    }

    private ResourceDictionary LoadDictionary(string code)
    {
        if (_cache.TryGetValue(code, out var cached)) return cached;

        var uri = new System.Uri($"pack://application:,,,/Locales/{code}.xaml");
        var dictionary = new ResourceDictionary { Source = uri };
        _cache[code] = dictionary;
        return dictionary;
    }

    /// <summary>取一条词条；找不到时返回键名本身，便于发现漏翻。</summary>
    public static string Get(string key)
        => Application.Current.TryFindResource(key) as string ?? key;

    /// <summary>按位置插值：F("key", a, b) 对应词条里的 {0} {1}。</summary>
    public static string F(string key, params object[] args)
    {
        var text = Get(key);
        try { return string.Format(text, args); }
        catch (FormatException) { return text; }
    }

    /// <summary>带占位符的词条：Get("X", ("n", 3)) → "..."。占位符用 {n} 表示。</summary>
    public static string Format(string key, params (string Name, object Value)[] args)
    {
        var text = Get(key);
        foreach (var (name, value) in args)
            text = text.Replace("{" + name + "}", value.ToString());
        return text;
    }
}

/// <summary>词条静态快捷访问，XAML 侧用 DynamicResource，C# 侧用 Lang.T("...")。</summary>
public static class Lang
{
    public static string T(string key) => LanguageService.Get(key);

    public static string T(string key, params (string Name, object Value)[] args)
        => LanguageService.Format(key, args);

    /// <summary>带 {0} {1} 占位符的词条。</summary>
    public static string F(string key, params object[] args) => LanguageService.F(key, args);
}
