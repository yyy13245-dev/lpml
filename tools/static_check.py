#!/usr/bin/env python3
"""LPML 静态校验：不编译，但尽量把会导致编译失败 / 运行崩溃的问题提前找出来。

检查项：
 1. C# 括号 / 引号配平、using 命名空间是否存在
 2. XAML 是否合法 XML、x:Key 是否重复
 3. XAML 的 x:Class 与后台代码 partial class 是否对应
 4. 后台代码里用到的 x:Name 控件是否在 XAML 中声明
 5. 引用的本地类型（new Xxx / 类型标注）是否在项目中有定义
 6. csproj / sln 引用的文件是否存在
"""
import os
import re
import sys
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src")
problems = []
warnings = []
info = []

def walk(ext):
    for base, _dirs, files in os.walk(SRC):
        for name in files:
            if name.endswith(ext):
                yield os.path.join(base, name)

cs_files = list(walk(".cs"))
xaml_files = list(walk(".xaml"))

# ───────────────────────── 1. C# 括号配平 ─────────────────────────
def strip_strings_and_comments(text):
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i+1] == '/':
            while i < n and text[i] != '\n':
                i += 1
            continue
        if c == '/' and i + 1 < n and text[i+1] == '*':
            i += 2
            while i + 1 < n and not (text[i] == '*' and text[i+1] == '/'):
                i += 1
            i += 2
            continue
        if c == '"':
            # 可能是逐字字符串 / 插值字符串
            if i and text[i-1] == '@':
                i += 1
                while i < n:
                    if text[i] == '"' and i + 1 < n and text[i+1] == '"':
                        i += 2
                        continue
                    if text[i] == '"':
                        i += 1
                        break
                    i += 1
                continue
            i += 1
            while i < n:
                if text[i] == '\\':
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        if c == "'":
            i += 1
            while i < n:
                if text[i] == '\\':
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)

pairs = {'}': '{', ')': '(', ']': '['}
for path in cs_files:
    raw = open(path, encoding='utf-8').read()
    code = strip_strings_and_comments(raw)
    stack = []
    line = 1
    for ch in code:
        if ch == '\n':
            line += 1
        if ch in '{([':
            stack.append((ch, line))
        elif ch in '})]':
            if not stack:
                problems.append(f"[括号] {os.path.relpath(path, ROOT)}:{line} 多余的 {ch}")
                break
            top, _ = stack.pop()
            if top != pairs[ch]:
                problems.append(f"[括号] {os.path.relpath(path, ROOT)}:{line} 期望闭合 {top} 但遇到 {ch}")
                break
    if stack:
        ch, line = stack[-1]
        problems.append(f"[括号] {os.path.relpath(path, ROOT)} 未闭合 {ch}（起始行 {line}）")

# ───────────────────────── 2. XAML 合法性 ─────────────────────────
XAML_NS = "{http://schemas.microsoft.com/winfx/2006/xaml}"
xaml_names = {}
xaml_classes = {}
for path in xaml_files:
    try:
        tree = ET.parse(path)
    except ET.ParseError as ex:
        problems.append(f"[XAML] {os.path.relpath(path, ROOT)} 解析失败：{ex}")
        continue
    root = tree.getroot()
    cls = root.get(XAML_NS + "Class")
    if cls:
        xaml_classes[path] = cls
    names = set()
    keys = []
    for element in root.iter():
        name = element.get(XAML_NS + "Name")
        if name:
            names.add(name)
        key = element.get(XAML_NS + "Key")
        if key:
            keys.append(key)
    dup = {k for k in keys if keys.count(k) > 1}
    if dup:
        problems.append(f"[XAML] {os.path.relpath(path, ROOT)} 重复的 x:Key：{sorted(dup)}")
    xaml_names[path] = names

# ───────────────────────── 3. x:Class ↔ 后台代码 ─────────────────────────
declared_types = set()
for path in cs_files:
    text = open(path, encoding='utf-8').read()
    for m in re.finditer(r'\b(?:public|internal|private|protected)?\s*(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+)*(?:class|record|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)', text):
        declared_types.add(m.group(1))

namespaces = {}
for path in cs_files:
    text = open(path, encoding='utf-8').read()
    m = re.search(r'^namespace\s+([A-Za-z0-9_.]+)\s*;', text, re.M)
    if m:
        namespaces[path] = m.group(1)

for path, cls in xaml_classes.items():
    if '.' in cls:
        ns, name = cls.rsplit('.', 1)
    else:
        ns, name = "", cls
    found = False
    for cs in cs_files:
        text = open(cs, encoding='utf-8').read()
        if re.search(rf'\bnamespace\s+{re.escape(ns)}\s*;', text) and re.search(rf'\bclass\s+{re.escape(name)}\b', text):
            found = True
            break
    if not found:
        problems.append(f"[XAML] {os.path.relpath(path, ROOT)} 的 x:Class \"{cls}\" 在后台代码中找不到对应 partial class")

# ───────────────────────── 4. 后台代码用到的 x:Name ─────────────────────────
# 只检查"成员访问链的起点"（前面不是 '.' 的标识符），避免把 Context.Settings.Text 之类误报
CHAIN_START = set("""
AppDomain MessageBoxButton MessageBoxImage MessageBoxResult MessageBoxOptions StringComparison StringSplitOptions
UriKind Visibility WindowState WindowStartupLocation ResizeMode BitmapCacheOption BitmapCreateOptions
Input Media Collections RegularExpressions InteropServices Runtime Serialization ComponentModel Globalization
Application MessageBox System Microsoft Task Dispatcher File Path Process Math CancellationToken Clipboard
Current ComboBox Grid Context LPML AppContext Directory File Path Environment ProcessStartInfo
Language Services Locales Brushes Keyboard ModifierKeys Color Brushes Colors KeyboardNavigation
Core Utils Views Controls Helpers Converters Download Game Auth Launch Instances Mods Integration Mirror Config
Java Text Id Items Groups Versions Environment Service Theme Settings Paths Accounts Http Mirrors Installer
Loaders ManifestService Modpacks Resolver Downloads Launcher Overrides CustomBaseUrls Children ColumnDefinitions
GridUnitType HorizontalAlignment VerticalAlignment Orientation Thickness CornerRadius
Seq Binding Xaml Wpf Win32 Forms Drawing Animation Shapes Documents Controls Primitives
""".split())

for xaml_path, names in xaml_names.items():
    cs_path = xaml_path + ".cs"
    if not os.path.exists(cs_path):
        continue
    code = open(cs_path, encoding='utf-8').read()
    # 去掉字符串字面量、逐行注释与 XML 文档注释，避免把 "String.Msg.X" 误判成类型引用
    code = re.sub(r'"(?:[^"\\\n]|\\.)*"', '""', code)
    code = re.sub(r'//[^\n]*', '', code)
    code = re.sub(r'///[^\n]*', '', code)
    missing = set()
    for m in re.finditer(r'(?<![.\w])([A-Z][A-Za-z0-9_]*)\s*\.', code):
        ident = m.group(1)
        if ident in names:
            continue
        if ident in CHAIN_START or ident in declared_types:
            continue
        missing.add(ident)
    if missing:
        warnings.append(f"[控件] {os.path.relpath(cs_path, ROOT)} 使用了未在 XAML 声明的标识符：{sorted(missing)}")

# ───────────────────────── 4b. XAML 事件处理器是否存在于后台代码 ─────────────────────────
EVENT_ATTRS = {"Click", "SelectionChanged", "Loaded", "Unloaded", "Checked", "Unchecked", "LostFocus",
               "GotFocus", "ValueChanged", "TextChanged", "MouseLeftButtonDown", "MouseLeftButtonUp",
               "MouseEnter", "MouseLeave", "SelectedItemChanged", "KeyDown", "KeyUp", "Drop", "DragEnter",
               "PreviewMouseDown", "PreviewMouseUp", "ScrollChanged", "RequestBringIntoView", "Initialized"}

for path in xaml_files:
    cs_path = path + ".cs"
    if not os.path.exists(cs_path):
        continue
    code = open(cs_path, encoding='utf-8').read()
    methods = set(re.findall(r'\b([A-Za-z_][A-Za-z0-9_]*)\s*\(', code))
    text = open(path, encoding='utf-8').read()
    for m in re.finditer(r'\b(' + "|".join(EVENT_ATTRS) + r')="([A-Za-z_][A-Za-z0-9_]*)"', text):
        handler = m.group(2)
        if handler not in methods:
            problems.append(f"[XAML] {os.path.relpath(path, ROOT)} 事件 {m.group(1)}=\"{handler}\" 在后台代码中找不到方法")

# ───────────────────────── 4c. StaticResource 键是否存在 ─────────────────────────
all_keys = set()
for path in xaml_files:
    text = open(path, encoding='utf-8').read()
    all_keys.update(re.findall(r'x:Key="([^"]+)"', text))

for path in xaml_files:
    text = open(path, encoding='utf-8').read()
    for m in re.finditer(r'\{StaticResource\s+([A-Za-z0-9_]+)\}', text):
        key = m.group(1)
        if key not in all_keys:
            problems.append(f"[XAML] {os.path.relpath(path, ROOT)} 引用了不存在的静态资源：{key}")
    for m in re.finditer(r'\{DynamicResource\s+([A-Za-z0-9_]+)\}', text):
        key = m.group(1)
        if key not in all_keys:
            warnings.append(f"[XAML] {os.path.relpath(path, ROOT)} 引用了未在 XAML 定义的动态资源（可能由代码注入）：{key}")

# ───────────────────────── 5. 本地类型引用 ─────────────────────────
bcl = set("""Task List Dictionary HashSet Queue Stack String StringBuilder Exception Object Enum Math Convert
Guid DateTime DateTimeOffset TimeSpan Uri Version Random Array Tuple HttpClient HttpRequestMessage HttpResponseMessage
StringContent FormUrlEncodedContent Stream StreamReader StreamWriter FileStream MemoryStream File Directory Path
Process ProcessStartInfo CancellationToken CancellationTokenSource SemaphoreSlim Progress Interlocked Stopwatch
MessageBox MessageBoxResult MessageBoxButton MessageBoxImage Application Window UserControl Page Button TextBox
PasswordBox ComboBox ComboBoxItem CheckBox Slider ProgressBar ListBox ListBoxItem TreeView TreeViewItem Expander
TabControl TabItem Border Grid StackPanel TextBlock ScrollViewer Image BitmapImage Rectangle Ellipse Path
SolidColorBrush Color Colors CornerRadius Thickness Duration Storyboard DoubleAnimation CubicEase ScaleTransform
TranslateTransform RotateTransform TransformGroup BlurEffect DropShadowEffect Visibility HorizontalAlignment
VerticalAlignment GridLength GridUnitType Style ResourceDictionary HierarchicalDataTemplate DataTemplate
ControlTemplate Trigger EventTrigger BeginStoryboard Setter ContentPresenter ItemsPresenter DataType
RoutedEventArgs RoutedPropertyChangedEventArgs SelectionChangedEventArgs PropertyChangedEventArgs EventArgs
Marshal DllImport StructLayout LayoutKind IntPtr Point Size Rect Brush FontFamily FontWeight FontSize
Clipboard OpenFileDialog SaveFileDialog FolderBrowserDialog DialogResult FolderBrowser NativeMethods
LauncherSettings MirrorSettings DownloadSettings TempSettings PathSettings JavaSettings LaunchSettings
AppearanceSettings AdvancedSettings AccountSettings ObservableCollection ZipArchive ZipFile ZipArchiveEntry
MemoryStatusEx AccentPolicy WindowCompositionAttributeData ComponentResourceKey FrameworkElement UIElement
ITypedElement Panel Canvas WrapPanel DockPanel UniformGrid Separator ToolTip ContextMenu MenuItem
JsonProperty JsonSerializer JsonSerializerOptions JsonCommentHandling JsonNumberHandling JsonIgnoreCondition
Regex Match Group CultureInfo IValueConverter Binding DoNothing VisibilityConverter PropertyPath
FromArgb ColorConverter ApplicationSettingsBase GroupBox Label RadioButton ToggleButton RepeatButton
ScrollBar Thumb Track Popup BorderThickness WindowInteropHelper IOException InvalidOperationException
TimeoutException FileInfo DirectoryInfo StreamReader StreamWriter MemoryStatusEx AccentPolicy
WindowCompositionAttributeData HttpClient HttpRequestMessage HttpResponseMessage FormUrlEncodedContent
StringContent MediaTypeWithQualityHeaderValue AuthenticationHeaderValue RangeHeaderValue HttpCompletionOption
HttpStatusCode WebProxy DecompressionMethods JsonElement JsonPropertyName JsonSerializerOptions
SolidColorBrush RotateTransform SkewTransform MatrixTransform EasingFunctionBase CubicEase QuadraticEase
DoubleAnimationUsingKeyFrames ColorAnimation ObjectAnimationUsingKeyFrames ThicknessAnimation
Run LineBreak Hyperlink Bold Italic Span Section Paragraph FlowDocument TextBlock
FileDialog OpenFileDialog SaveFileDialog FolderBrowserDialog DialogResult CommonDialog
BitmapImage BitmapSource BitmapFrame CroppedBitmap TransformedBitmap RenderTargetBitmap
WriteableBitmap ImageSource DrawingImage DrawingGroup GeometryDrawing RectangleGeometry EllipseGeometry
PathGeometry StreamGeometry PathFigure LineSegment PolyLineSegment ArcSegment BezierSegment
RadialGradientBrush LinearGradientBrush GradientStop ImageBrush VisualBrush TileBrush
DispatcherTimer DispatcherOperation DispatcherFrame ComponentDispatcher
MemoryStream BufferedStream GZipStream DeflateStream BrotliStream
ZipArchive ZipArchiveEntry ZipFile ZipFileExtensions
SecureString CryptographicException SHA1 SHA256 MD5 HashAlgorithm RandomNumberGenerator
ManagementObjectSearcher PerformanceCounter EventLog
SpeechSynthesizer SoundPlayer MediaPlayer MediaTimeline
ObservableCollection ReadOnlyObservableCollection ObservableDictionary CancellationTokenRegistration GC
Progress Tpl Dataflow Channels
ValueTask TaskCompletionSource TaskFactory TaskScheduler
ConcurrentDictionary ConcurrentQueue ConcurrentBag ConcurrentStack BlockingCollection
Lazy ThreadLocal AsyncLocal CancellationTokenRegistration
MemoryPool ArrayPool Buffer TextEncoder TextDecoder
ProcessModule ProcessThread ProcessPriorityClass
RegistryKey Registry RegistryHive RegistryView
Timer Stopwatch TimeZoneInfo DateTimeKind Calendar Week
NumberFormatInfo CompareInfo CultureTypes
StringBuilder Interlocked Volatile SpinLock SpinWait Semaphore Mutex ManualResetEventSlim
ArgumentException ArgumentNullException ArgumentOutOfRangeException NotSupportedException OperationCanceledException
UnauthorizedAccessException DirectoryNotFoundException FileNotFoundException EndOfStreamException
OverflowException FormatException IndexOutOfRangeException NullReferenceException KeyNotFoundException
TaskCanceledException HttpRequestException WebException SocketException
Attribute Flags FlagsAttribute ObsoleteAttribute
NotifyIcon ContextMenuStrip ToolStripMenuItem
PrintDialog PrintQueue XpsDocument
BitmapEncoder PngBitmapEncoder JpegBitmapEncoder
PixelFormats BitmapPalette
RenderOptions VisualBitmapScalingMode BitmapScalingMode EdgeMode
TextOptions TextFormattingMode TextRenderingMode TextHintingMode
Fonts FontStretch FontStyle TextDecoration TextDecorations
Keyboard FocusManager NavigationWindow Frame PageFunction
ApplicationSettingsBase SettingsAttribute
DependencyProperty DependencyObject Freezable Animatable Visual UIElement3D
TriggerBase DataTrigger MultiTrigger MultiDataTrigger EventTrigger
ItemsPanelTemplate ControlTemplate ItemsPanelTemplate
VisualStateManager VisualState VisualStateGroup VisualTransition
Adorner AdornerLayer AdornerDecorator
ScrollContentPresenter VirtualizingStackPanel VirtualizingPanel
DataGrid DataGridColumn DataGridTextColumn DataGridTemplateColumn
Calendar DatePicker Expander GroupBox TabControl
Label RadioButton ToggleButton RepeatButton Slider TickBar
PasswordBox RichTextBox TextBox TextBoxBase
ComboBox ComboBoxItem ListBox ListBoxItem ListView ListViewItem GridView GridViewColumn
TreeView TreeViewItem Menu MenuItem ContextMenu ToolBar ToolBarTray StatusBar
ProgressBar RangeBase Thumb Track ScrollBar ScrollViewer
Window Dialog Launcher
Matrix Matrix3D Quaternion Vector Vector3D Point3D Rect3D Size3D
ThicknessAnimationBase SplineDoubleKeyFrame LinearDoubleKeyFrame DiscreteDoubleKeyFrame
EasingDoubleKeyFrame EasingColorKeyFrame
Storyboard Timeline TimelineGroup ParallelTimeline Clock ClockGroup
BeginStoryboard StopStoryboard PauseStoryboard ResumeStoryboard SeekStoryboard SetStoryboardSpeedRatio
RemoveStoryboard SkipStoryboardToFill ChangeProperty
SoundPlayerAction
ResourceDictionaryKey ThemeDictionaryExtension StaticResourceExtension DynamicResourceExtension
BindingBase RelativeSource TemplateBindingExtension
XmlDataProvider ObjectDataProvider CollectionViewSource
""".split())

for path in cs_files:
    text = open(path, encoding='utf-8').read()
    for m in re.finditer(r'\bnew\s+([A-Z][A-Za-z0-9_]*)\s*[\(\[]', text):
        t = m.group(1)
        if t not in declared_types and t not in bcl:
            warnings.append(f"[类型] {os.path.relpath(path, ROOT)} 引用了未定义的类型 new {t}(...)")

# ───────────────────────── 6. csproj / sln 引用 ─────────────────────────
for path in walk(".csproj"):
    try:
        ET.parse(path)
    except ET.ParseError as ex:
        problems.append(f"[csproj] {os.path.relpath(path, ROOT)} 解析失败：{ex}")
        continue
    text = open(path, encoding='utf-8').read()
    base_dir = os.path.dirname(path)
    for m in re.finditer(r'Include="([^"]+)"', text):
        value = m.group(1)
        if value.startswith('$') or ';' in value or '*' in value:
            continue
        if value.endswith('.csproj'):
            target = os.path.normpath(os.path.join(base_dir, value.replace('\\', '/')))
            if not os.path.exists(target):
                problems.append(f"[csproj] {os.path.relpath(path, ROOT)} 引用的项目不存在：{value}")
        elif value.endswith('.ico') or value.endswith('.png'):
            target = os.path.normpath(os.path.join(base_dir, value.replace('\\', '/')))
            if not os.path.exists(target):
                problems.append(f"[csproj] {os.path.relpath(path, ROOT)} 引用的资源不存在：{value}")

# ResourceDictionary Source 引用检查
for path in xaml_files:
    text = open(path, encoding='utf-8').read()
    base_dir = os.path.dirname(path)
    for m in re.finditer(r'(?<![A-Za-z])Source="([^"]+)"', text):
        value = m.group(1)
        if value.startswith('pack:') or value.startswith('/'):
            continue
        target = os.path.normpath(os.path.join(base_dir, value.replace('/', os.sep)))
        if not os.path.exists(target):
            problems.append(f"[XAML] {os.path.relpath(path, ROOT)} 引用的资源字典不存在：{value}")


# ───────────────────────── 7. using 解析（抓 CS0246：类型找不到） ─────────────────────────
def _strip(t):
    t = re.sub(r'@?"(?:[^"\\\n]|\\.)*"', '""', t)
    t = re.sub(r'//[^\n]*', '', t)
    t = re.sub(r'///[^\n]*', '', t)
    return t

def _ancestors(ns):
    parts = ns.split('.')
    out = set()
    for i in range(len(parts), 0, -1): out.add('.'.join(parts[:i]))
    return out | {""}

for proj in ("LPML.Core", "LPML.App"):
    pfiles = [f for f in cs_files if os.sep + proj + os.sep in f]
    if not pfiles: continue
    decl = {}
    ns_of = {}
    for f in pfiles:
        text = open(f, encoding='utf-8').read()
        m = re.search(r'^namespace\s+([\w\.]+)\s*;', text, re.M)
        ns = m.group(1) if m else ""
        ns_of[f] = ns
        for dm in re.finditer(r'\b(?:public|internal|private)?\s*(?:sealed\s+|abstract\s+|static\s+|partial\s+|readonly\s+)*(?:class|record|struct|interface|enum)\s+(\w+)', text):
            decl.setdefault(dm.group(1), set()).add(ns)
    for f in pfiles:
        raw = open(f, encoding='utf-8').read()
        code = _strip(raw)
        vis = _ancestors(ns_of[f]) | set(re.findall(r'^using\s+([\w\.]+)\s*;', raw, re.M))
        idents = set(re.findall(r'(?<![.\w])([A-Z]\w*)', code))
        miss = sorted({n for n in idents if n in decl and not (decl[n] & vis)})
        if miss:
            problems.append(f"[using] {os.path.relpath(f, ROOT)} 用了未导入的项目类型 {miss}（会得到 CS0246）")
        dup = [n for n in idents if n in decl and len([d for d in decl[n] if d in vis]) > 1]
        if dup:
            problems.append(f"[using] {os.path.relpath(f, ROOT)} 存在歧义类型 {sorted(dup)}（CS0104）")

# ───────────────────────── 8. catch 顺序（CS0160）与重复定义（CS0128） ─────────────────────────
DERIVED = {
 'Exception': ['IOException','UnauthorizedAccessException','InvalidOperationException','ArgumentException',
   'OperationCanceledException','TaskCanceledException','FormatException','OverflowException',
   'System.ComponentModel.Win32Exception','ObjectDisposedException','PlatformNotSupportedException',
   'NotSupportedException','ArgumentNullException','ArgumentOutOfRangeException','JsonException'],
 'OperationCanceledException': ['TaskCanceledException'],
 'IOException': ['FileNotFoundException','DirectoryNotFoundException','EndOfStreamException'],
 'ArgumentException': ['ArgumentNullException','ArgumentOutOfRangeException'],
}
for path in cs_files:
    lines = open(path, encoding='utf-8').read().split('\n')
    i = 0
    while i < len(lines):
        if re.match(r'\s*catch\b', lines[i]):
            group = []
            j = i
            while j < len(lines) and re.match(r'\s*(catch|finally)\b', lines[j]):
                if re.match(r'\s*finally', lines[j]): break
                m = re.search(r'catch\s*\(\s*([\w\.]+)', lines[j])
                group.append((j + 1, m.group(1) if m else 'Exception'))
                j += 1
            for a in range(len(group)):
                for b in range(a + 1, len(group)):
                    ta, tb = group[a][1], group[b][1]
                    if tb in DERIVED.get(ta, []) or (ta == 'Exception' and tb != 'Exception'):
                        problems.append(f"[catch] {os.path.relpath(path, ROOT)}:{group[b][0]} catch({ta}) 后又 catch({tb})（CS0160）")
            i = max(j, i + 1)
        else:
            i += 1
    # 连续完全重复的 3 行块（多为复制事故 → CS0128）
    for i in range(len(lines) - 3):
        blk = lines[i:i+3]
        if blk[0].strip().startswith('var ') and blk == lines[i+3:i+6]:
            warnings.append(f"[重复] {os.path.relpath(path, ROOT)}:{i+1} 疑似重复 3 行块：{blk[0].strip()[:40]}")

# ───────────────────────── 9. 游离成员（方法声明出现在类型体外 → CS1519/CS1513） ─────────────────────────
for path in cs_files:
    lines = open(path, encoding='utf-8').read().split('\n')
    depth = 0
    in_ns = False
    for i, line in enumerate(lines):
        stripped = line.strip()
        if re.match(r'^namespace\s', stripped): in_ns = True
        if depth == 0 and in_ns and re.match(r'(private|public|internal|protected)\s+[\w\<\>\[\]?\.]+\s+\w+\s*\(', stripped):
            problems.append(f"[游离] {os.path.relpath(path, ROOT)}:{i+1} 方法声明在类型体外：{stripped[:50]}（CS1519）")
        depth += len(re.findall(r'\{', line)) - len(re.findall(r'\}', line))

# ── 10. BCL 类型缺 using（排除完全限定名用法，避免误报） ──
BCL = {
 'System.IO': ['StreamWriter','StreamReader','FileStream','MemoryStream','FileInfo','DirectoryInfo'],
 'System.Text': ['StringBuilder','Encoding'],
 'System.Diagnostics': ['Process','ProcessStartInfo','Stopwatch'],
 'System.Text.RegularExpressions': ['Regex','RegexOptions','Match','MatchCollection'],
 'System.Collections.ObjectModel': ['ObservableCollection','ReadOnlyObservableCollection'],
 'System.ComponentModel': ['INotifyPropertyChanged','PropertyChangedEventArgs','CancelEventArgs'],
 'System.Runtime.InteropServices': ['Marshal','DllImport','StructLayout'],
 'System.Security.Cryptography': ['SHA256','SHA1','MD5'],
 'System.Text.Json': ['JsonSerializer','JsonDocument','JsonElement'],
 'System.IO.Compression': ['ZipFile','ZipArchive','ZipArchiveEntry'],
 'System.Net.Sockets': ['TcpClient','Socket','AddressFamily'],
}
IMPLICIT = {'System','System.Collections.Generic','System.IO','System.Linq','System.Net.Http','System.Threading','System.Threading.Tasks'}
for path in cs_files:
    text = open(path, encoding='utf-8').read()
    code = _strip(text)
    has = set(re.findall(r'^using\s+([\w\.]+)\s*;', text, re.M)) | IMPLICIT
    for ns, types in BCL.items():
        if ns in has: continue
        for t in types:
            # 完全限定名用法（System.Text.Regex）不需要 using，跳过
            if ns + '.' + t in code: continue
            if re.search(r'(?<![.\w])' + t + r'\b', code):
                warnings.append(f"[BCL] {os.path.relpath(path, ROOT)} 用到了 {t}，可能需要 using {ns};")

# ── 11. XAML 初始化期 NRE（事件在 InitializeComponent 期间触发，引用了后声明的控件）──
def _body_range(code, handler):
    hm = re.search(r'(?:private|public|internal)\s+(?:void|async\s+void)\s+' + re.escape(handler) + r'\s*\([^)]*\)\s*\{', code)
    if not hm: return None
    i = code.index('{', hm.start()); d = 0
    for j in range(i, len(code)):
        if code[j] == '{': d += 1
        elif code[j] == '}':
            d -= 1
            if d == 0: return (i, j)
    return None

for xf in xaml_files:
    cs = xf + '.cs'
    if not os.path.exists(cs): continue
    t = open(xf, encoding='utf-8').read()
    code = open(cs, encoding='utf-8').read()
    names = [(m.start(), m.group(1)) for m in re.finditer(r'x:Name="(\w+)"', t)]
    order = {n: i for i, (_, n) in enumerate(names)}
    if not order: continue
    for m in re.finditer(r'(\w+)="(\w+)"', t):
        attr, handler = m.group(1), m.group(2)
        if attr not in ('ValueChanged', 'SelectionChanged', 'Checked', 'Unchecked', 'TextChanged', 'IsEnabledChanged'): continue
        if not re.search(r'\b' + handler + r'\s*\(', code): continue
        seg = t[:m.start()]
        elem = t[seg.rfind('<'):m.start()]
        mm = re.search(r'x:Name="(\w+)"', elem)
        if not mm: continue
        owner = mm.group(1)
        rng = _body_range(code, handler)
        if not rng: continue
        body = code[rng[0]:rng[1]]
        for n in set(re.findall(r'\b(' + '|'.join(map(re.escape, order)) + r')\b', body)):
            if order[n] > order[owner] and not re.search(re.escape(n) + r'\s+is\s+null', body):
                problems.append(f"[NRE] {os.path.relpath(xf, ROOT)} <{owner}={handler}> 引用后声明的 {n} 且无 null 守卫")

# ── 12. 对只读计算属性的绑定必须显式 OneWay（否则运行时抛 InvalidOperationException）──
readonly_props = set()
for path in cs_files:
    if os.sep + 'LPML.Core' + os.sep not in path: continue
    text = open(path, encoding='utf-8').read()
    for m in re.finditer(r'public\s+(?:string|bool|int|long|double)\s+(\w+)\s*=>', text):
        readonly_props.add(m.group(1))
if readonly_props:
    pat = re.compile(r'\{Binding\s+(' + '|'.join(sorted(map(re.escape, readonly_props))) + r')\s*([,}])')
    for path in xaml_files:
        text = open(path, encoding='utf-8').read()
        for m in pat.finditer(text):
            tail = m.group(2)
            if tail == ',' and 'Mode=OneWay' in text[m.start():m.start()+60]: continue
            if tail == '}':
                problems.append(f"[绑定] {os.path.relpath(path, ROOT)} 对只读属性 {m.group(1)} 的绑定未指定 Mode=OneWay")

# ───────────────────────── 输出 ─────────────────────────
info.append(f"C# 文件：{len(cs_files)} 个，XAML 文件：{len(xaml_files)} 个")

print("=" * 68)
print("LPML 静态校验报告")
print("=" * 68)
for line in info:
    print("  " + line)
print()
if problems:
    print(f"❌ 错误 {len(problems)} 项：")
    for p in problems:
        print("   " + p)
else:
    print("✅ 未发现阻断性问题（括号配平 / XML 合法性 / 引用完整性）")

print()
if warnings:
    print(f"⚠️  提示 {len(warnings)} 项：")
    for w in warnings[:40]:
        print("   " + w)
    if len(warnings) > 40:
        print(f"   … 还有 {len(warnings) - 40} 条")
else:
    print("✅ 无类型 / 控件引用提示")

sys.exit(1 if problems else 0)
