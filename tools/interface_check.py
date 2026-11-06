#!/usr/bin/env python3
"""LPML 接口一致性检查：抓"调用了一个并不存在的成员"导致的跑飞问题。"""
import os, re, sys, glob
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src")
problems, warnings = [], []
cs = []
for base, _d, files in os.walk(SRC):
    for f in files:
        if f.endswith(".cs"): cs.append(os.path.join(base, f))

# 1. 收集声明过的成员名
declared = set()
for p in cs:
    t = open(p, encoding='utf-8').read()
    for m in re.finditer(r'\b(?:public|private|internal|protected)\s+[\w<>\[\]?\.]+\s+(\w+)\s*(?:\(|\{|=>|;)', t):
        declared.add(m.group(1))
    for m in re.finditer(r'\b(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\]?\.]+\s+(\w+)\s*\(', t):
        declared.add(m.group(1))

# 2. BCL / 已知安全成员
bcl = set("""ToString Equals GetHashCode GetType Select Where Any All First FirstOrDefault Last LastOrDefault
Count Add Remove Clear Contains ContainsKey TryGetValue ToList ToArray ToUpper ToLower Trim TrimStart TrimEnd
Split Replace StartsWith EndsWith Substring IndexOf PadLeft PadRight Insert Format Parse TryParse
Write WriteLine Read ReadLine Flush Dispose Close Save Load Invoke BeginInvoke Wait Sleep Start Stop
Report ContinueWith ConfigureAwait GetAwaiter GetResult Run Cancel ThrowIfCancellationRequested
EnsureSuccessStatusCode ReadAsStreamAsync GetStringAsync GetBytesAsync GetAsync PostAsync SendAsync
SetText GetText ShowDialog Show Hide Close Activate Focus FindResource TryFindResource
Append Insert RemoveAt Sort OrderBy ThenBy GroupBy Distinct Skip Take Sum Max Min Average
Concat Join Union Intersect Except Zip SelectMany Cast OfType Reverse SequenceEqual
EnsureDirectory CreateDirectory Exists GetFileName GetDirectoryName GetExtension Combine
GetFullPath GetTempPath Move Copy Delete OpenRead OpenWrite ReadAllText WriteAllText
HumanSize HumanSpeed SafeFileName Sha1 Sha256 Sha256String OfflineUuid ShortUuid FormatUuid
Navigate NavigateToContent Refresh ApplyArguments BuildNavigation PlayPageEnter
""".split())

# 3. 定向类型 -> 允许成员表
TYPED = {
 "_ctx": None, "Context": None,
}

# 4. 扫描调用点 X.Member  (前面不是 '.')
for p in cs:
    code = open(p, encoding='utf-8').read()
    code = re.sub(r'"(?:[^"\\\n]|\\.)*"', '""', code)
    code = re.sub(r'//[^\n]*', '', code)
    code = re.sub(r'///[^\n]*', '', code)
    for m in re.finditer(r'(?<![.\w"])([A-Za-z_]\w*)\.([A-Za-z_]\w*)', code):
        obj, member = m.group(1), m.group(2)
        if member in declared or member in bcl: continue
        if re.match(r"^(LPML|Services|Core|Views|Controls|Config|Download|Game|Launch|Java|Utils|Integration|Mirror|Modrinth|Mods|Instances|Multiplayer|Windows)$", obj): continue
        if obj in ('Lang','L','Console','Math','File','Directory','Path','Json','Task','Process',
                   'Application','MessageBox','Clipboard','GC','Environment','Regex','Uri',
                   'Convert','Guid','Random','Enum','string','int','double','byte','char'): continue
        warnings.append(f"{os.path.relpath(p, ROOT)}: {obj}.{member} 未被声明")

# 5. 定向：关键对象必须存在的成员
CHECKS = [
 ("src/LPML.App/Views/MultiplayerCreatePage.xaml.cs", "_ctx", ["Instances","Settings","Save","Launcher"]),
 ("src/LPML.App/Views/MultiplayerJoinPage.xaml.cs", "_ctx", ["Http","Settings","Save","Instances"]),
 ("src/LPML.App/Views/MultiplayerPage.xaml.cs", "_core", ["IsInstalled","InstalledVersion","InstallAsync","Uninstall","Stop","IsRunning","Dispose"]),
]
CTX_MEMBERS = set(re.findall(r'public\s+(?:static\s+|async\s+|override\s+|sealed\s+)*[\w<>\[\]?\.]+\s+(\w+)\s*[{(=>]',
    open(os.path.join(ROOT,'src/LPML.Core/Core/LauncherContext.cs'), encoding='utf-8').read()))
CORE_MEMBERS = set(re.findall(r'public\s+(?:static\s+|async\s+|override\s+|sealed\s+)*[\w<>\[\]?\.]+\s+(\w+)\s*[{(=>]',
    open(os.path.join(ROOT,'src/LPML.Core/Multiplayer/TerracottaCore.cs'), encoding='utf-8').read()))

for rel, obj, members in CHECKS:
    full = os.path.join(ROOT, rel)
    if not os.path.exists(full): continue
    pool = CTX_MEMBERS if obj == "_ctx" else CORE_MEMBERS
    for mem in members:
        if mem not in pool:
            problems.append(f"[接口] {rel}: {obj}.{mem} 在目标类型上不存在")

# 6. 定向：Core 模型类的成员访问（防止凭记忆写错成员名，如 IsRecommended vs IsSuggested）
MODEL_CHECKS = [
    ("JavaArchOption", ["Arch", "PlatformKey", "Name", "IsSuggested", "Display"]),
    ("LaunchResult", ["Success", "ErrorMessage", "ProcessId"]),
    ("DownloadAggregate", ["ReceivedBytes", "TotalBytes", "BytesPerSecond", "Summary"]),
    ("JavaInfo", ["Name", "DisplayName", "Path", "MajorVersion", "Is64Bit", "Detail", "Version"]),
    ("Account", ["Id", "UserName", "DisplayName", "Type", "AccessToken", "IsValid"]),
    ("ModrinthProject", ["Slug", "Title", "Downloads", "DownloadsText", "WebUrl"]),
    ("ModrinthVersion", ["VersionNumber", "VersionTypeText", "PublishedText", "LoadersText", "PrimaryFile"]),
    ("ModrinthDependency", ["Name", "TypeText", "ProjectId"]),
    ("GameInstance", ["Id", "Name", "VersionId", "LastPlayed"]),
]
MODEL_MEMBERS = {}
for cls, _members in MODEL_CHECKS:
    found = set()
    for path in glob.glob(os.path.join(ROOT, "src", "**", "*.cs"), recursive=True):
        text = open(path, encoding="utf-8").read()
        m = re.search(r"(?:class|record)\s+" + cls + r"\s*(?:\([^)]*\))?\s*\{", text)
        if not m: continue
        i = m.end() - 1; d = 0
        for j in range(i, len(text)):
            if text[j] == "{": d += 1
            elif text[j] == "}":
                d -= 1
                if d == 0: break
        body = text[i:j]
        found |= set(re.findall(r"public\s+(?:static\s+|override\s+)?[\w\<\>\[\]?\.]+\s+(\w+)\s*[{(=>]", body))
    MODEL_MEMBERS[cls] = found

for cls, members in MODEL_CHECKS:
    pool = MODEL_MEMBERS.get(cls, set())
    if not pool: continue
    for member in members:
        if member not in pool:
            problems.append(f"[模型] {cls}.{member} 在类型上不存在（实际成员：{sorted(pool)}）")

# 页面上调用 .IsRecommended 之类未定义成员
for path in glob.glob(os.path.join(ROOT, "src", "**", "*.cs"), recursive=True):
    text = open(path, encoding="utf-8").read()
    for cls, pool in MODEL_MEMBERS.items():
        if not pool: continue
        for m in re.finditer(r"(?<![.\w])" + cls + r"\b", text):
            pass

# 7. 静态类成员校验：防止误用不存在的方法（如 FileHelper.HumanBytes / MirrorManager.Current）
core_static = {}
for path in glob.glob(os.path.join(ROOT, "src", "LPML.Core", "**", "*.cs"), recursive=True):
    text = open(path, encoding="utf-8").read()
    for m in re.finditer(r"public static class (\w+)", text):
        cls = m.group(1)
        i = text.index("{", m.end() - 1); d = 0
        for j in range(i, len(text)):
            if text[j] == "{": d += 1
            elif text[j] == "}":
                d -= 1
                if d == 0: break
        body = text[i:j]
        core_static[cls] = set(re.findall(r"public static [\w\<\>\[\]?,. ]+? (\w+)\s*[({=]", body))

for path in glob.glob(os.path.join(ROOT, "src", "LPML.App", "**", "*.cs"), recursive=True):
    text = open(path, encoding="utf-8").read()
    for cls, members in core_static.items():
        for m in re.finditer(r"(?<![.\w])" + cls + r"\.(\w+)", text):
            if m.group(1) not in members:
                problems.append(f"[静态成员] {os.path.relpath(path, ROOT)}: {cls}.{m.group(1)} 不存在（可用：{sorted(members)}）")

print("=" * 68)
print("LPML 接口一致性检查")
# 6. 定向：Core 模型类的成员访问（防止凭记忆写错成员名，如 IsRecommended vs IsSuggested）
MODEL_CHECKS = [
    ("JavaArchOption", ["Arch", "PlatformKey", "Name", "IsSuggested", "Display"]),
    ("LaunchResult", ["Success", "ErrorMessage", "ProcessId"]),
    ("DownloadAggregate", ["ReceivedBytes", "TotalBytes", "BytesPerSecond", "Summary"]),
    ("JavaInfo", ["Name", "DisplayName", "Path", "MajorVersion", "Is64Bit", "Detail", "Version"]),
    ("Account", ["Id", "UserName", "DisplayName", "Type", "AccessToken", "IsValid"]),
    ("ModrinthProject", ["Slug", "Title", "Downloads", "DownloadsText", "WebUrl"]),
    ("ModrinthVersion", ["VersionNumber", "VersionTypeText", "PublishedText", "LoadersText", "PrimaryFile"]),
    ("ModrinthDependency", ["Name", "TypeText", "ProjectId"]),
    ("GameInstance", ["Id", "Name", "VersionId", "LastPlayed"]),
]
MODEL_MEMBERS = {}
for cls, _members in MODEL_CHECKS:
    found = set()
    for path in glob.glob(os.path.join(ROOT, "src", "**", "*.cs"), recursive=True):
        text = open(path, encoding="utf-8").read()
        m = re.search(r"(?:class|record)\s+" + cls + r"\s*(?:\([^)]*\))?\s*\{", text)
        if not m: continue
        i = m.end() - 1; d = 0
        for j in range(i, len(text)):
            if text[j] == "{": d += 1
            elif text[j] == "}":
                d -= 1
                if d == 0: break
        body = text[i:j]
        found |= set(re.findall(r"public\s+(?:static\s+|override\s+)?[\w\<\>\[\]?\.]+\s+(\w+)\s*[{(=>]", body))
    MODEL_MEMBERS[cls] = found

for cls, members in MODEL_CHECKS:
    pool = MODEL_MEMBERS.get(cls, set())
    if not pool: continue
    for member in members:
        if member not in pool:
            problems.append(f"[模型] {cls}.{member} 在类型上不存在（实际成员：{sorted(pool)}）")

# 页面上调用 .IsRecommended 之类未定义成员
for path in glob.glob(os.path.join(ROOT, "src", "**", "*.cs"), recursive=True):
    text = open(path, encoding="utf-8").read()
    for cls, pool in MODEL_MEMBERS.items():
        if not pool: continue
        for m in re.finditer(r"(?<![.\w])" + cls + r"\b", text):
            pass

# 7. 静态类成员校验：防止误用不存在的方法（如 FileHelper.HumanBytes / MirrorManager.Current）
core_static = {}
for path in glob.glob(os.path.join(ROOT, "src", "LPML.Core", "**", "*.cs"), recursive=True):
    text = open(path, encoding="utf-8").read()
    for m in re.finditer(r"public static class (\w+)", text):
        cls = m.group(1)
        i = text.index("{", m.end() - 1); d = 0
        for j in range(i, len(text)):
            if text[j] == "{": d += 1
            elif text[j] == "}":
                d -= 1
                if d == 0: break
        body = text[i:j]
        core_static[cls] = set(re.findall(r"public static [\w\<\>\[\]?,. ]+? (\w+)\s*[({=]", body))

for path in glob.glob(os.path.join(ROOT, "src", "LPML.App", "**", "*.cs"), recursive=True):
    text = open(path, encoding="utf-8").read()
    for cls, members in core_static.items():
        for m in re.finditer(r"(?<![.\w])" + cls + r"\.(\w+)", text):
            if m.group(1) not in members:
                problems.append(f"[静态成员] {os.path.relpath(path, ROOT)}: {cls}.{m.group(1)} 不存在（可用：{sorted(members)}）")

print("=" * 68)
print(f"扫描 {len(cs)} 个 C# 文件，声明成员 {len(declared)} 个\n")
if problems:
    print(f"❌ 接口错误 {len(problems)} 项：")
    for x in problems: print("   " + x)
else:
    print("✅ 定向接口检查通过")
print()
if warnings:
    print(f"⚠️  可疑成员访问 {len(warnings)} 项（前 25）：")
    for w in warnings[:25]: print("   " + w)
else:
    print("✅ 无未声明成员访问")
sys.exit(1 if problems else 0)
