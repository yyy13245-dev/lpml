# LPML User Guide

**LPML = Let's Play Minecraft Launcher**

A Minecraft Java Edition launcher written in .NET 10 + WPF. Portable: it writes nothing to C:, the registry, or AppData — copy the whole folder to a USB drive and go.

---

## 1. First Run

> On first launch a **User Agreement and Disclaimer** is shown. In the GUI, read it in full and click **Agree and Continue**; in the CLI, type `是` / `我同意` / `同意` / `yes`. Otherwise exit and delete the software. You can re-read it from the **Documents** page at any time, or with the `agreement` command in the CLI.

1. **Check Java**: the launcher scans PATH, JAVA_HOME and common install locations.
   If none is found, install one from **Download → Java Runtime**, or add it manually at **Settings → Java & Memory**.
2. **Install the game**: go to **Download → Install Game** and **double-click** a version in the list.
3. **Fill in the install page**: save name, loader (choose *Vanilla* if you don't want one), instance folder.
4. **Click "Install"**: a **Tasks** button appears in the top-right corner; open it for live progress.
5. **Launch**: back on the Play page, pick an instance and hit the launch button.

> Minecraft version → Java mapping (a mismatch won't start):
> 1.16 and older → Java 8 | 1.17 → 16 | 1.18–1.19 → 17 | 1.20+ → 21 | 26w snapshots → 25

---

## 2. Play Page

- **Instance**: pick which instance to run. With no instance, the top shows a hint and settings panels are greyed out.
- **Launch**: the bottom-right button floats on every page. Clicking it elsewhere jumps here; clicking it here launches directly.
- **Mute**: one click sets every volume in that instance's `options.txt` to 0.
  Turn it back up inside the game. When already muted the button reads "Muted" and is disabled.
- **Multiple instances**: several instances can run at the same time. The launch button always launches
  and never turns into a close button.
- **End game**: while any instance is running, an **⏹ End game** button appears at the bottom right.
  Click it for a small panel listing every running instance — select one and click **End selected**.
- **Instance dropdown**: newly created instances show up right away; just pick one, no need to visit
  the Instances page first.
- **Separate logs**: when several games run at once, every game log line is prefixed with its instance name.

### How long does launching take

A progress window opens after you click launch and **only closes once the game reaches the main menu**:

| Stage | What happens |
|---|---|
| ① Dependencies | Checks missing files and downloads them (count, speed, ETA) |
| ② Signing in | Microsoft for premium, skin site for external, offline passes straight through |
| ③ Preparing | Builds JVM arguments and the classpath |
| ④ Starting | Java is running; the window shows the last few MC log lines |
| ⑤ Closing | Closes when main-menu output is detected (e.g. `Reloading ResourceManager`) |

The launch button, instance dropdown and mute button are all greyed out during this, and restored afterwards.

---

## 3. Download Center

### Install Game

The version list shows its status (how many versions). **Double-click** a version to open the install page:

- **Save name**: what it's called after install, i.e. the instance name. Picking a loader appends a
  suffix (`1.21.4` → `1.21.4-fabric`), **but never once you've typed your own name**.
- **Loader**: Vanilla / Fabric / Quilt / Forge / NeoForge / OptiFine.
  Loader versions are fetched for the MC version you picked, so mismatches can't happen.
- **Instance folder**: where to install; `.minecraft` by default.
- **Duplicate names are reported immediately** rather than after installing.

> The loader no longer has its own page — choose it right on the install page.

### Modpack Import

- **Local file**: Modrinth `.mrpack`, HMCL / MultiMC / Prism exports, plain zips.
  CurseForge packs import their framework and config, but mods must be added manually
  (`manifest.json` carries no download URLs).
- **Online**: search Modrinth directly, sorted by downloads. Double-click to open the install page,
  then pick **MC version → modpack version → loader** (only loaders the author shipped).

### Download Mods

The Download Center has a **Download mods** tab, and the instance's Mods page has a
**⬇ Download mods** button. Both open the same page.

- **Install into**: defaults to the instance currently selected on the home page. Pick a different
  one and the mod goes into that instance's `mods` folder instead.
- **Search**: type a name and press Enter or click **Search**. Results come from Modrinth, sorted by downloads.
- **Version**: clicking a mod lists every version on the right, with versions matching the
  instance's MC version and loader sorted first. The instance filter only narrows search by
  loader, so mods that work with your modpack are not silently dropped. If a modpack does not
  record its loader, the launcher infers it from `fabric.mod.json` / `mods.toml` in installed mods.
- **Pages**: 20 results per page. **Previous / Next** walks through every matching result;
  visited pages are cached and requests are spaced out so rapid paging cannot hammer Modrinth.
- After **Download and install**, the file appears in the Task Center with its own progress, speed and status.

### Managing installed mods

The small checkbox at the end of each row in **Instances → Mods** starts multi-select:

- Checking one row reveals a toolbar with **Select all / Enable all / Disable all /
  Delete all / Cancel selection** and the number of selected mods. Deleting asks for confirmation.
- **Select all** becomes **Clear selection** after one click, and toggles back on the next.
- A single mod can also be removed via right-click → **Delete this mod**, or its row's **Delete** button.

### When downloads fail

Two errors show up most often:

| Error | Meaning | What happens |
|---|---|---|
| **403 / 404** | This mirror doesn't have the file | Automatically retries on another mirror, falling back to official |
| **429** | Rate limited | Backs off automatically; if it persists lower the concurrency (e.g. 12) |

BMCLAPI being flaky is normal. To switch: Settings → Download Source — Official / BMCLAPI / FastMCMirror. MCBBS has shut down and is no longer offered.

---

## 4. Task Center

The top-right **Tasks** button **only appears when there are tasks**. Clicking it opens three levels:

```
Task list (one operation = one task group)
  Install game            Downloading 82/312
  ▓▓▓▓▓░░░░
      ↓ click
Detail → Download: groups first, each with its own progress bar
  📦 Modpack content     312/640 files   ▓▓▓▓▓░░░
  🎮 Vanilla game files   82/120 files   ▓▓▓▓▓▓░░
      ↓ click either group
  Every file in that group: name, progress bar, percentage, speed, status
Detail → Install: click "Installation steps" for a flow chart of every step
```

Installing a modpack downloads both mods and vanilla game files, so they are split into two
cards; per-file progress only expands after you click one, keeping thousands of files readable.
Standalone mod or Java downloads go into "Other files".

- **Right-click menu**: both groups and single files support Retry / Redownload / Cancel.
- Every download (mods, modpacks, vanilla, Java) lands here.
- Use "Clear" when done so they don't pile up.

---

## 5. Instances & Folders

### Instance folders

Create as many folders as you like; each is a **complete game directory**
(`versions / libraries / assets / mods / saves / config`, ...).

- Paths support **absolute, relative (to the launcher root) and environment variables** (`%USERPROFILE%\...`).
- The **Instances → Folders** page is the single place to create / delete folders, set the current
  one, list the instances and versions actually inside it, and complete game files for a selected instance.
  Instance Settings no longer switches folders.
- Resolution order: **explicit directory > instance folder > isolation dir > default `.minecraft`**.

### Instance isolation

Isolation data lives in **`.minecraft\instances\<instance-id>\`**, so copying `.minecraft` moves everything.

| Mode | Version files | mods / saves / config | Traits |
|---|---|---|---|
| **User data only** (default) | `.minecraft`, shared | Instance dir | Saves space |
| **Full isolation** | Instance dir | Instance dir | Copy and go, uses space |

- Global: Settings → Paths → Isolation mode
- Per instance: Instance Settings → Isolation (may "follow global")
- Migrate between modes in one click; hard links on the same volume, so no extra space.

### Per-instance lpml.json

Each instance directory gets an `lpml.json` holding its name, version, loader and isolation mode.
It travels with the directory — copy the folder to someone else and the settings come along.

---

## 6. Accounts

| Type | Notes |
|---|---|
| **Microsoft** | Device code flow, tokens refresh automatically. **Bring your own Azure Client ID** |
| **Offline** | Zero setup. UUID **can be customised**: position 15 (`3`) and 20 (`8/9/a/b`) are protocol-fixed and auto-corrected; the other 30 chars are free |
| **External** | Any Yggdrasil / authlib-injector server; javaagent injected at launch |
| **LittleSkin** | Built-in official API, works out of the box |

**Where accounts are stored**: each account remembers its own location and they can be mixed.

- Portable (default): `<root>\LPML\accounts\` — never touches the system drive
- User profile: `%USERPROFILE%\LPML\accounts\` — survives moving the launcher
- Custom: must be imported manually first

---

## 7. Settings

### Download
- **Concurrency**: how many files at once, default 32
- **Adaptive concurrency by file size** (on by default): more for small files, fewer for large
  ```
  30 KB library → 48    2 MB asset → 32    over 5 MB → 8
  ```
- **Gentle ramp-up** (on by default): starts at 4 and climbs every 240 ms, full in 1.7 s.
  Avoids dozens of simultaneous TLS handshakes and keeps mirrors from rate-limiting you
- **Segmented download**: large files split into parallel chunks, 8 segments above 2 MB
- Global speed limit, proxy, optional "ignore certificate errors"

### Download Source
Three options: Official / BMCLAPI / FastMCMirror, each settable per resource type (17 kinds). MCBBS has shut down; old configs fall back to BMCLAPI automatically.

### Paths
Game directory, cache directory, isolation mode and migration.

### Java & Memory
- The list labels each entry (`JDK 21 · Eclipse Temurin · 64-bit`)
- **Auto-retry with another Java on failure**: same version first (JDK↔JRE), never downgrades
- Memory: automatic or manual

### Launch
- MC window title: uses the client's native `--title`; needs the checkbox on
- After launch: keep / minimize / exit
- Integrity check and one-click repair

### Appearance
- **Performance mode** (topmost — it overrides backdrop / blur / animations below):
  - **Standard**: full visuals — backdrop, translucent cards, eased animations
  - **Smooth**: lighter — cards become opaque, backdrop and background blur are off,
    hover feedback **snaps** between two states (no tweening), only the page fade remains,
    animated backgrounds stop at the first frame
  - You can also switch with launch switches (**persisted to config**):
    ```
    LPML.exe --smooth     switch to Smooth
    LPML.exe --standard   switch back to Standard
    ```
- **Launcher title**: the launcher's own window title, applies on focus loss. Empty restores `LPML`
- **Background**: png / jpg / jpeg / bmp / **gif (animated)**, adjustable opacity and fit
- Theme (dark / light / system), accent colour, animation toggle

### Advanced
Interface language (applies instantly, no restart), log level, TEMP policy.

---

## 8. Command Line

Launching with `--cli` enters command-line mode with no window:

> On the first run (including one-shot queries such as `--list` / `--help`) the full *User Agreement & Disclaimer* is displayed. Only `是` / `我同意` / `同意` / `yes` accepts it; any other input closes the CLI. Consent is shared with the GUI and stored in `launcher.json`.

```
LPML.exe --cli [options]

  --instance, -i <name/ID>   Use an instance
  --version,  -v <version>   Launch by version directly
  --account,  -a <username>  Use an account
  --after <keep|hide|exit>   Behaviour after MC starts
  --list, ls                 List instances
  --list-versions            List installed versions
  --help                     Help
```

With no options, an **interactive shell** opens in its own console window; the fixed bottom hint only mentions `help`:
type `help` for the full command list (about 50 commands covering instances, accounts, version installs, mods,
downloads, Java, settings and docs). Common ones are `ls`, `launch <instance>`, `quit` and `clear`.

Once the game starts, the same window becomes the **game REPL**: game logs scroll above, the input line stays usable.
The same command set remains available (type `help`); unrecognized input is forwarded to the game.

One-shot queries such as `--list` and `--help` do not open a separate window; their output goes to the current cmd
so it can be copied or redirected. When output is redirected, the interactive UI falls back to plain line input.

---

## 9. Directory Layout

```
<root>\
  ├─ LPML.exe                 # Program
  ├─ .minecraft\              # Game data (same layout as official / PCL / HMCL)
  │   ├─ versions\ libraries\ assets\ saves\ mods\ runtime\
  │   └─ instances\           # Instance isolation data
  │        └─ <id>\ lpml.json
  └─ LPML\                    # Launcher's own data
      ├─ config\              # Settings, instances, folders, library index
      ├─ accounts\            # Accounts (default location)
      ├─ cache\               # Chunk temp files, installers
      ├─ backgrounds\         # Custom background image
      └─ logs\                # lpml.log + oldlog1~5.zip
```

**Logs**: if `lpml.log` has content at startup it's packed into `oldlog1.zip` and older archives shift
down, keeping at most 5. Every line carries a number like `[#0042]` — just quote the number when reporting.

---

## 10. FAQ

**Q: Launch says the version JSON is missing?**
Usually the game directory was deleted, or the instance points elsewhere. The log names the path it checked.
The launcher also looks for the same version in other known directories and reuses it (hard link, no re-download).

**Q: "No usable Java 0 found"?**
A misleading message from older builds. Now it states plainly whether the version JSON or client jar is missing.
If it still appears, send the numbered log.

**Q: Downloads are slow?**
1. Click **Settings → Download → Apply recommended settings**
2. Try concurrency around 12 — too high triggers rate limiting
3. Switch mirrors; FastMCMirror is usually steady

**Q: Where did my mods go?**
With isolation on, they're in that instance's `mods` (`.minecraft\instances\<id>\mods`);
otherwise `.minecraft\mods`. **Instance Settings → Overview** has an "Open mods folder" button.

**Q: Can I share the game directory with other launchers?**
Yes — it's the standard layout used by PCL / HMCL / the official launcher.
You can also add their directories as **library reuse sources** (read-only) to avoid re-downloading.

**Q: Why are there two LPML entries in Task Manager?**
One is the launcher, the other is its log guard (a child process, so Windows groups them together).
The guard writes `LPML\logs\lpml.log`, and if the UI freezes past the threshold it asks whether to
force-close the launcher. It exits by itself when the launcher closes — nothing is left running.
To disable it: **Settings → Advanced → Enable log guard process** (takes effect after restart).

---

## 11. Known Limitations

1. Microsoft login needs your own Azure Client ID (none is bundled or managed for you)
2. Forge / NeoForge install via the official installer, so a working Java is required
3. OptiFine's official installer is GUI-only; it's launched for you to click through
4. CurseForge modpack mods must be added manually (`manifest.json` has no URLs)
5. "Check for updates" isn't wired to a release source yet
6. Guard settings (enable / threshold / auto-close) take effect **after a restart**
7. Running several launchers at once shares one `lpml.log`, so their entries get mixed

---

## Appendix: Multiplayer (Terracotta / EasyTier)

1. Open "Multiplayer" in the sidebar and click "Download core". The default route fetches the official v0.4.2 bundle automatically (CN mirrors first); no URL needed.
2. **Host**: open "Create room", launch an instance, then press Esc → "Open to LAN" in game. Click "Create room" and share the invitation code once it appears.
3. **Guest**: open "Join room", paste the code (`U/XXXX-XXXX-XXXX-XXXX`) and click "Join room". Once joined, launch the game and double-click the "Terracotta lobby" in the Multiplayer list.
4. Codes are compatible with HMCL / PCL CE / FCL. Connectivity depends on the other launcher's version and network; interoperability is not guaranteed.
5. Under "Settings" you can override the download URL and version, add custom public nodes (one per line), or point at HMCL's downloaded core folder to reuse the same binary.

P2P is less reliable than relayed servers. It is meant for playing with friends, not as a replacement for a Minecraft Java Edition server.

> Terracotta © Burning_TNT and contributors · AGPL-3.0-or-later (with exception); built on EasyTier.
> LPML talks to the unmodified official binary over its local HTTP API and shows the copyright notice in the UI.

## Disclaimer

This is an unofficial third-party tool, unaffiliated with or unendorsed by Mojang Studios, Microsoft, Modrinth, BMCLAPI or any mirror provider. The software and its documentation were generated mainly with AI assistance and are provided "AS IS", without warranties of any kind.

You assume all risks of downloading, installing and launching the game and mods, signing in, making network requests and reading or writing files. Back up your worlds and configs yourself. Full terms are in `DISCLAIMER.md` in the program folder, and in the *User Agreement and Disclaimer* on the **Documents** page.
