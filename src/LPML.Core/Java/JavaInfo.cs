namespace LPML.Core.Java;

public sealed class JavaInfo
{
    /// <summary>稳定标识（路径的 SHA1 前 16 位），用于列表去重与「设为默认」。</summary>
    public string Id { get; set; } = "";

    /// <summary>java 可执行文件的完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>JDK/JRE 根目录（可执行文件上两级），用于「自动同步整个目录」。</summary>
    public string JavaHome { get; set; } = "";

    public string Version { get; set; } = "";
    public int MajorVersion { get; set; }
    public bool Is64Bit { get; set; } = true;

    /// <summary>是完整 JDK（含编译器），还是仅运行时 JRE。</summary>
    public bool IsJdk { get; set; } = true;

    /// <summary>类型显示文本：JDK / JRE。</summary>
    public string TypeText => IsJdk ? "JDK" : "JRE";

    public string Vendor { get; set; } = "";
    public JavaSource Source { get; set; } = JavaSource.Scan;

    /// <summary>用户自定义备注名；为空时显示自动生成的名称。</summary>
    public string? Nickname { get; set; }

    /// <summary>是否已被验证可用（文件存在且能跑 java -version）。</summary>
    public bool IsValid { get; set; } = true;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>这个 Java 是不是带控制台的 java.exe（调试启动时要用它看输出）。</summary>
    public bool IsConsoleVariant =>
        Path.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>显示名称：优先备注，否则 类型 版本 · 发布者 · 架构。</summary>
    public string Name =>
        !string.IsNullOrWhiteSpace(Nickname)
            ? Nickname!
            : $"{TypeText} {MajorVersion} · {Vendor} · {(Is64Bit ? L.T("String.Core.Item.Bit64") : L.T("String.Core.Item.Bit32"))}";

    /// <summary>
    /// 列表里区分 java.exe / javaw.exe 的显示名。
    /// 同一个 JDK 会同时被扫出这两个，不标注的话列表里就是两条一模一样的名字。
    /// </summary>
    public string ListName
    {
        get
        {
            var baseName = Name;
            if (string.IsNullOrWhiteSpace(Path)) return baseName;
            var exe = Path.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase) ? "javaw" : "java";
            return $"{baseName}  [{exe}]";
        }
    }

    /// <summary>列表主标题（同 Name，供界面统一调用）。</summary>
    public string DisplayName => Name;

    /// <summary>列表副标题：版本串 + 路径。</summary>
    public string Detail => string.IsNullOrWhiteSpace(Version) ? Path : $"{Version} — {Path}";

    public string SourceText => Source switch
    {
        JavaSource.Environment => L.T("String.Java.Source.Environment"),
        JavaSource.GameRuntime => L.T("String.Java.Source.GameRuntime"),
        JavaSource.Downloaded => L.T("String.Java.Source.Downloaded"),
        JavaSource.Manual => L.T("String.Java.Source.Manual"),
        _ => L.T("String.Java.Source.Scan")
    };

    public string StatusText => IsValid
        ? L.T("String.Java.Status.Valid")
        : L.T("String.Java.Status.Invalid");

    public JavaInfo Clone() => (JavaInfo)MemberwiseClone();
}

public enum JavaSource
{
    /// <summary>扫描常见安装目录得到。</summary>
    Scan,

    /// <summary>来自 JAVA_HOME 或 PATH。</summary>
    Environment,

    /// <summary>游戏目录 runtime（Mojang 官方运行时）。</summary>
    GameRuntime,

    /// <summary>用户手动指定。</summary>
    Manual,

    /// <summary>启动器下载。</summary>
    Downloaded
}

/// <summary>同一版本同时存在 JDK 与 JRE 时的偏好。</summary>
public enum JavaRuntimePreference
{
    /// <summary>优先用 JDK（默认）。</summary>
    PreferJdk = 0,

    /// <summary>优先用 JRE（体积更小、启动略快）。</summary>
    PreferJre = 1
}

/// <summary>Java 启动失败后的处理策略。</summary>
public enum JavaFailureAction
{
    /// <summary>自动换一个 Java 重新启动（默认）。</summary>
    RetryWithAnother = 0,

    /// <summary>不重启，直接抛错。</summary>
    Throw = 1
}

/// <summary>多 Java 的选择策略。</summary>
public enum JavaSelectionMode
{
    /// <summary>按版本要求自动挑选最合适的一个（推荐）。</summary>
    Auto = 0,

    /// <summary>始终使用用户指定的那一个。</summary>
    Manual = 1
}
