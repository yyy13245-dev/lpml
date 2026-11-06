# Markdown Syntax Support

Documents shown in Settings are rendered by the built-in Markdown parser. This page lists
**the syntax it supports** — write your own documents using these rules and they will display
correctly.

The parser is built into the launcher: **no network access, no web content loaded**, so pages
open instantly and carry none of the risks of embedding a web view.

---

## Block Syntax

### Headings

Use `#` through `######`; level one is the largest:

```
# Level 1
## Level 2
### Level 3
```

### Paragraphs

Consecutive lines merge into one paragraph; a blank line starts a new one.

### Lists

Both **bullet** and **ordered** lists are supported, and they **nest by indentation**:

```
- Level one
  - Level two
    - Level three
1. First
2. Second
```

Markers change with depth: bullets go `●` → `○` → `■`, ordered go `1.` → `a.` → `i.`.

> A more-indented line that isn't a new list item is treated as a continuation
> of the previous item (lazy continuation) rather than a new paragraph.

### Code Blocks

Fence with three backticks (or three tildes). A **language tag is allowed** and is not rendered:

````
```csharp
var x = 1;
```
````

The closing fence must be **no shorter** than the opening one, so a ``` inside the code
won't end the block prematurely.

### Tables

```
| Column A | Column B |
|---|---|
| a | b |
```

The separator row is required — a line is only treated as a table if the next line really is
a separator. **So a stray `|` in ordinary text won't break the layout.**

### Blockquotes

Start lines with `>`; they render with an **accent-coloured left bar and a light background**.
Multiple lines keep their line breaks.

### Horizontal Rules

A line of `---`, `***` or `___`.

---

## Inline Syntax

| Syntax | Result |
|---|---|
| `` `code` `` | Monospace |
| `**bold**` | **bold** |
| `*italic*` | *italic* |
| `***both***` | bold containing italic |
| `[text](url)` | Clickable link |

### Nesting works

`**bold containing *italic***` renders correctly as **bold wrapping italic**.

A single regex can't express nesting, so the parser is a **hand-written scanner** that recurses
into the content whenever it meets a marker.

### No false positives

Asterisks in `a*b*c` and `2 * 3 = 6` are **not** treated as italic —
a `*` only opens emphasis when the character to its left is not an ASCII letter or digit.

> That check deliberately does **not** exclude CJK characters:
> italics still work in Chinese text. Treating "letters" as including Han characters
> would break every italic span in a Chinese document.

### Escaping

Prefix a literal `*` or `` ` `` with a backslash: `\*`.

---

## Where Documents Live

Document files live in the launcher's `docs` folder, split into `zh-CN` and `en-US`
subfolders. To read the raw file or open it in an editor such as Typora, use
"Open Containing Folder" at the top right of any document page.

Documents follow the language you picked for the interface; if a translation is missing,
the other language is shown instead, so you never get a blank page.

---

## Known Limitations

- Code blocks are monospace with **no coloured syntax highlighting**
- No images, footnotes, task lists or raw HTML
- Tables are simple pipe tables; no line breaks inside cells or merged cells
- Rows with fewer cells leave the missing ones empty
- A `|` inside a paragraph is only treated as a table when the next line is a table
  separator row; ordinary text keeps the bar as-is

---

## Disclaimer

This software and its documentation were generated mainly with AI assistance. It is unofficial third-party software provided "AS IS" without warranties of any kind; you assume all risks of use. Full terms are in `DISCLAIMER.md` and the *User Agreement and Disclaimer*.
