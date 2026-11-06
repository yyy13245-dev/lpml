using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LPML.App.Utils;

/// <summary>
/// 极简 Markdown → FlowDocument 渲染器。只为显示内置使用说明，
/// 支持：标题、段落、多级列表（可嵌套）、围栏代码块（带语言标识）、表格、
/// 引用（带左边框）、分隔线，以及行内的 代码 / 粗体 / 斜体 / 链接（可嵌套）。
/// 不引入第三方依赖，也不做 HTML 渲染。
///
/// 行内标记用手写扫描器而不是一条大正则：
/// 正则无法表达嵌套（**粗体里有*斜体***），也会把 a*b*c 这类乘法误判成斜体。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    public static FlowDocument Render(string markdown, double fontSize = 14)
    {
        var document = new FlowDocument
        {
            FontSize = fontSize,
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
            LineHeight = fontSize * 1.65,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };

        if (string.IsNullOrWhiteSpace(markdown)) return document;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line)) { index++; continue; }

            // 分隔线 --- / ***
            if (Regex.IsMatch(line, @"^\s*(-{3,}|\*{3,}|_{3,})\s*$"))
            {
                document.Blocks.Add(new BlockUIContainer(new System.Windows.Controls.Separator
                {
                    Margin = new Thickness(0, 14, 0, 14),
                    Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128))
                }));
                index++;
                continue;
            }

            // 围栏代码块：``` 或 ```csharp —— 语言标识只用于判断围栏长度，不渲染出来
            var fence = Regex.Match(line, @"^\s*(`{3,}|~{3,})\s*(?<lang>[\w+#.-]*)\s*$");
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value[0];
                var fenceLength = fence.Groups[1].Value.Length;
                index++;
                var code = new StringBuilder();
                while (index < lines.Length && !IsClosingFence(lines[index], marker, fenceLength))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }
                index++; // 跳过收尾围栏
                document.Blocks.Add(new Paragraph(new Run(code.ToString().TrimEnd('\n')) { FontFamily = MonoFont })
                {
                    FontSize = fontSize - 1,
                    Padding = new Thickness(14, 10, 14, 10),
                    Margin = new Thickness(0, 6, 0, 12),
                    Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128))
                });
                continue;
            }

            // 标题 # ...
            var heading = Regex.Match(line, @"^\s*(#{1,6})\s+(.*)$");
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                var size = level switch { 1 => fontSize + 12, 2 => fontSize + 7, 3 => fontSize + 3, _ => fontSize + 1 };
                var paragraph = new Paragraph
                {
                    FontSize = size,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, level <= 2 ? 22 : 14, 0, 8)
                };
                AppendInlines(paragraph, heading.Groups[2].Value.Trim());
                document.Blocks.Add(paragraph);
                index++;
                continue;
            }

            // 表格：当前行含 | 且下一行是分隔行
            if (line.Contains('|') && IsTableStart(lines, index))
            {
                document.Blocks.Add(BuildTable(lines, ref index, fontSize));
                continue;
            }

            // 引用 >
            if (line.TrimStart().StartsWith('>'))
            {
                var quoted = new StringBuilder();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    quoted.AppendLine(Regex.Replace(lines[index], @"^\s*>\s?", ""));
                    index++;
                }
                var text = quoted.ToString().Trim();
                var block = new Paragraph
                {
                    Margin = new Thickness(0, 6, 0, 12),
                    Padding = new Thickness(14, 8, 12, 8),
                    BorderBrush = AccentBrush(),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128))
                };
                // 引用内部按行拆成多段，保留软换行
                var first = true;
                foreach (var inner in text.Split('\n'))
                {
                    if (!first) block.Inlines.Add(new LineBreak());
                    foreach (var inline in ParseInlines(inner.TrimEnd())) block.Inlines.Add(inline);
                    first = false;
                }
                document.Blocks.Add(block);
                continue;
            }

            // 列表（支持按缩进嵌套）
            var item = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
            if (item.Success)
            {
                document.Blocks.Add(BuildList(lines, ref index, item.Groups[1].Value.Length, 0, fontSize));
                continue;
            }

            // 普通段落：连续非空行合并为一段。
            // 只有"确实是表格起始"才因 | 断开——段落里随手打个竖杠不该炸排版。
            var buffer = new StringBuilder();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                var current = lines[index];
                if (Regex.IsMatch(current, @"^#{1,6}\s")
                    || current.TrimStart().StartsWith('>')
                    || Regex.IsMatch(current, @"^\s*([-*+]|\d+[.)])\s+")
                    || Regex.IsMatch(current, @"^\s*(`{3,}|~{3,})")
                    || (current.Contains('|') && IsTableStart(lines, index))
                    || Regex.IsMatch(current, @"^\s*(-{3,}|\*{3,}|_{3,})\s*$")) break;
                buffer.Append(current).Append('\n');
                index++;
            }
            if (buffer.Length > 0)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 4, 0, 10) };
                AppendInlines(paragraph, buffer.ToString().Trim());
                document.Blocks.Add(paragraph);
            }
        }

        return document;
    }

    // ── 行内解析 ───────────────────────────────────────────────
    // 手写扫描器，支持嵌套，且不会把 a*b*c 误判成斜体。

    private static void AppendInlines(Paragraph paragraph, string text)
    {
        foreach (var inline in ParseInlines(text)) paragraph.Inlines.Add(inline);
    }

    /// <summary>解析行内标记，返回 Inline 集合。</summary>
    private static List<Inline> ParseInlines(string? text)
    {
        var result = new List<Inline>();
        if (string.IsNullOrEmpty(text)) return result;
        Scan(result, text, 0, text.Length, 0);
        return result;
    }

    /// <summary>
    /// 递归扫描 [start, end) 区间的行内标记。
    /// 处理顺序：转义 → 行内代码 → 链接 → 粗体 → 斜体 → 纯文本。
    /// </summary>
    private static void Scan(List<Inline> output, string text, int start, int end, int depth)
    {
        if (depth > 8) { AddText(output, text[start..end]); return; }

        var plain = new StringBuilder();
        var i = start;

        void Flush()
        {
            if (plain.Length == 0) return;
            AddText(output, plain.ToString());
            plain.Clear();
        }

        while (i < end)
        {
            var c = text[i];

            // 转义 \*
            if (c == '\\' && i + 1 < end)
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // 行内代码 `code`
            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1 && close < end)
                {
                    Flush();
                    output.Add(new Run(text[(i + 1)..close]) { FontFamily = MonoFont });
                    i = close + 1;
                    continue;
                }
            }

            // 链接 [文字](地址)
            if (c == '[')
            {
                var closeBracket = text.IndexOf(']', i + 1);
                if (closeBracket > i + 1 && closeBracket + 1 < end && text[closeBracket + 1] == '(')
                {
                    var closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket + 2 && closeParen <= end)
                    {
                        Flush();
                        output.Add(BuildLink(text[(i + 1)..closeBracket],
                            text[(closeBracket + 2)..closeParen], depth));
                        i = closeParen + 1;
                        continue;
                    }
                }
            }

            // 粗体 **  / 斜体 *
            if (c == '*')
            {
                var bold = i + 1 < end && text[i + 1] == '*';
                var delimiter = bold ? "**" : "*";

                // 斜体的开定界符要求左侧不是字母数字，否则 a*b*c 会被当成斜体
                if (!bold && !IsLeftFlanking(text, i, start)) { plain.Append(c); i++; continue; }

                var close = FindClosing(text, i + delimiter.Length, end, bold);
                if (close < 0) { plain.Append(c); i++; continue; }

                var contentStart = i + delimiter.Length;
                if (contentStart >= close) { plain.Append(c); i++; continue; }

                Flush();
                var inner = new List<Inline>();
                Scan(inner, text, contentStart, close, depth + 1);
                output.Add(bold ? Wrap(inner, true) : Wrap(inner, false));
                i = close + delimiter.Length;
                continue;
            }

            plain.Append(c);
            i++;
        }

        Flush();
    }

    /// <summary>找到配对的 * 或 **；找不到返回 -1。</summary>
    private static int FindClosing(string text, int from, int end, bool bold)
    {
        for (var j = from; j < end; j++)
        {
            if (text[j] != '*') continue;

            if (bold)
            {
                if (j + 1 < end && text[j + 1] == '*')
                {
                    // 遇到连续三颗星（***）时，真正的粗体闭合在后一颗：
                    // **粗体包含*斜体*** → 前两颗属于"斜体闭合 + 粗体闭合"，
                    // 这样内容才会带上完整的 *斜体*，递归时才能渲染出嵌套的斜体。
                    if (j + 2 < end && text[j + 2] == '*') return j + 1;
                    return j;
                }
                continue;
            }

            // 斜体闭合：右侧不能是空白，且内容非空
            if (j == from) continue;
            if (char.IsWhiteSpace(text[j - 1])) continue;
            return j;
        }
        return -1;
    }

    /// <summary>
    /// 开定界符是否成立：左侧是行首、空白、标点或汉字时都算。
    /// 只挡 ASCII 字母数字——目的是让 <c>a*b*c</c> 这类乘法不被当成斜体；
    /// 但不能连汉字也挡，否则"这是*重点*内容"里的斜体永远渲染不出来。
    /// </summary>
    private static bool IsLeftFlanking(string text, int position, int start)
    {
        if (position <= start) return true;
        return !IsAsciiAlphanumeric(text[position - 1]);
    }

    private static bool IsAsciiAlphanumeric(char ch)
        => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');

    private static void AddText(List<Inline> output, string value)
    {
        if (!string.IsNullOrEmpty(value)) output.Add(new Run(value));
    }

    /// <summary>把内部 Inline 包成粗体 / 斜体（多个就先套一层 Span）。</summary>
    private static Inline Wrap(List<Inline> inner, bool bold)
    {
        // Inline 只允许有一个父级，所以多段内容先塞进一个 Span 再整体包一层
        Inline content = inner.Count switch
        {
            0 => new Run(""),
            1 => inner[0],
            _ => Combine(inner)
        };

        return bold ? new Bold(content) : new Italic(content);
    }

    private static Span Combine(List<Inline> inner)
    {
        var span = new Span();
        foreach (var inline in inner) span.Inlines.Add(inline);
        return span;
    }

    private static Inline BuildLink(string label, string url, int depth)
    {
        var link = new Hyperlink();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) link.NavigateUri = uri;
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = e.Uri.ToString(), UseShellExecute = true });
            }
            catch { /* 打不开就算了 */ }
        };

        var inner = new List<Inline>();
        Scan(inner, label, 0, label.Length, depth + 1);
        if (inner.Count == 0) link.Inlines.Add(new Run(label));
        else foreach (var inline in inner) link.Inlines.Add(inline);

        return link;
    }

    // ── 块级构造 ───────────────────────────────────────────────

    /// <summary>构造列表，按缩进递归支持嵌套。</summary>
    private static List BuildList(string[] lines, ref int index, int indent, int depth, double fontSize)
    {
        var ordered = Regex.IsMatch(lines[index], @"^\s*\d+[.)]\s+");
        var list = new List
        {
            MarkerStyle = ordered ? MarkerForOrdered(depth) : MarkerForUnordered(depth),
            Margin = new Thickness(indent > 0 ? 6 : 20, 6, 0, 10)
        };

        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) break;

            var match = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
            if (!match.Success) break;

            var currentIndent = match.Groups[1].Value.Length;
            if (currentIndent < indent) break;

            // 更深的缩进 → 作为上一项的子列表
            if (currentIndent > indent)
            {
                var sub = BuildList(lines, ref index, currentIndent, depth + 1, fontSize);
                var last = list.ListItems.LastOrDefault();
                if (last is not null) last.Blocks.Add(sub);
                else list.ListItems.Add(new ListItem(new Paragraph(new Run(match.Groups[3].Value))));
                continue;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
            AppendInlines(paragraph, match.Groups[3].Value);
            var item = new ListItem(paragraph);
            list.ListItems.Add(item);
            index++;

            // 缩进更多且不是新列表项 → 当作本项的续行
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                var next = lines[index];
                var nextIndent = next.Length - next.TrimStart().Length;
                if (nextIndent <= indent) break;
                if (Regex.IsMatch(next, @"^\s*([-*+]|\d+[.)])\s+")) break;
                paragraph.Inlines.Add(new Run(" " + next.Trim()));
                index++;
            }
        }

        return list;
    }

    private static TextMarkerStyle MarkerForUnordered(int depth) => depth switch
    {
        0 => TextMarkerStyle.Disc,
        1 => TextMarkerStyle.Circle,
        _ => TextMarkerStyle.Square
    };

    private static TextMarkerStyle MarkerForOrdered(int depth) => depth switch
    {
        0 => TextMarkerStyle.Decimal,
        1 => TextMarkerStyle.LowerLatin,
        _ => TextMarkerStyle.LowerRoman
    };

    /// <summary>下一行是不是表格的分隔行（|---|---|）。</summary>
    private static bool IsTableStart(string[] lines, int index)
        => index + 1 < lines.Length && Regex.IsMatch(lines[index + 1], @"^\s*\|?[\s:\-\|]+\|[\s:\-\|]*$");

    /// <summary>是否为收尾围栏：同一符号且长度不短于起始围栏。</summary>
    private static bool IsClosingFence(string line, char marker, int length)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < length) return false;
        return trimmed.All(ch => ch == marker);
    }

    /// <summary>Markdown 表格 → Grid（包在 BlockUIContainer 里）。</summary>
    private static Block BuildTable(string[] lines, ref int index, double fontSize)
    {
        var rows = new List<string[]>();
        while (index < lines.Length && lines[index].Contains('|'))
        {
            var raw = lines[index].Trim();
            if (Regex.IsMatch(raw, @"^\s*\|?[\s:\-\|]+\|[\s:\-\|]*$")) { index++; continue; }
            rows.Add(raw.Trim('|').Split('|').Select(c => c.Trim()).ToArray());
            index++;
        }
        if (rows.Count == 0) return new Paragraph();

        var columns = rows.Max(r => r.Length);
        var grid = new System.Windows.Controls.Grid();
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var value = c < rows[r].Length ? rows[r][c] : "";
                var block = new System.Windows.Controls.TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = fontSize - 0.5,
                    Margin = new Thickness(10, 7, 10, 7),
                    FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal
                };
                foreach (var inline in ParseInlines(value)) block.Inlines.Add(inline);
                System.Windows.Controls.Grid.SetRow(block, r);
                System.Windows.Controls.Grid.SetColumn(block, c);
                grid.Children.Add(block);
            }
        }

        return new BlockUIContainer(new System.Windows.Controls.Border
        {
            Child = grid,
            Margin = new Thickness(0, 6, 0, 14),
            BorderBrush = new SolidColorBrush(Color.FromArgb(45, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        });
    }

    /// <summary>取强调色画刷用于引用左边框；取不到就用灰色。</summary>
    private static Brush AccentBrush()
    {
        if (Application.Current?.TryFindResource("Brush.Accent") is Brush accent) return accent;
        return new SolidColorBrush(Color.FromArgb(140, 128, 128, 128));
    }
}
