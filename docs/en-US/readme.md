# LPML — Let's Play Minecraft Launcher

A Minecraft Java Edition launcher built with **.NET 10 + WPF**: a clean interface with features tucked
into a hierarchical menu, customisation on par with PCL / HMCL, multi-threaded segmented downloads with
size-adaptive concurrency, and switchable download sources across
**Official / BMCLAPI / FastMCMirror** (settable per resource type).

> MCBBS (`download.mcbbs.net`) has shut down and no longer resolves. It has been removed from the
> selectable sources, speed probes and failover candidates. Old configs still containing it are
> automatically switched to BMCLAPI at startup.

> **AI-generated notice**: the source code, UI strings, user guides and documentation of this project were **generated mainly with AI assistance**. They have not undergone a complete human audit, security review or systematic testing, and may contain defects, omissions, outdated information or unexpected behaviour. Assess the risks before use and do not rely on this project where stability, security or data integrity matter. You bear all consequences of using it. See [DISCLAIMER.md](/DISCLAIMER.md).

> **Unofficial project**: LPML is a third-party Minecraft launcher, unaffiliated with, unauthorised by and unendorsed by Mojang Studios, Microsoft, Modrinth, BMCLAPI, or any mirror, mod or modpack author. This repository distributes no game files, account credentials or third-party copyrighted assets.

> **LPML = Let's Play Minecraft Launcher**
> Portable: nothing is written to the system drive, the registry, or AppData — copy the folder to a USB drive and go.

**Repository**: <https://github.com/yyy13245-dev/lpml>

**Licence**: GNU GPL-3.0-or-later (SPDX: `GPL-3.0-or-later`) · full text in [LICENSE](/LICENSE) · copyright & attribution in [NOTICE](/NOTICE)

**Requirements**: Windows 10/11 (x64) · [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for framework-dependent builds · a Java runtime matching your Minecraft version

**Contents**: [Highlights](#-highlights) · [Quick Start](#-quick-start) · [Build](#-build) · [Project Layout](#-project-layout) · [Known Limitations](#️-known-limitations) · [Licence and Disclaimer](#-licence-and-disclaimer) · [Full disclaimer](/DISCLAIMER.md) · [NOTICE](/NOTICE)

---

## ⚡ Quick Start

1. Download a release archive from this repository's Releases page; if none is available yet, build from source with the commands below. Afterwards, **Settings → Online Update** can check for newer builds so you don't need to re-download the whole package.
2. Extract it to any folder and run `LPML.exe`. **On first launch a User Agreement and Disclaimer is shown**; read it and agree to continue (GUI: click **Agree and Continue**; CLI: type `是` / `我同意` / `同意` / `yes`).
3. Install a version from **Download → Install Game**, then pick the instance on the **Play** page and launch.

This repository contains no game files, account credentials or binary releases. Runtime data (config, accounts, logs, cache) stays inside the launcher folder and is excluded via `.gitignore`.

---

## ✨ Highlights

### Interface
- Mica / Acrylic **backdrop** (DWM system backdrop, falls back to "background image + Gaussian blur")
- Borderless custom title bar, rounded corners, dark / light / system theme, custom accent colour
- **Stretch animations**: card and button hover scaling, press-and-rebound, staggered list entrance, page fade-in
- All animations can be disabled or timed in Settings → Appearance
- The **Play button floats** in the bottom-right corner on every page
- **Custom background**: png / jpg / jpeg / bmp / **gif (animated, original frame delays restored)**,
  with adjustable opacity (default 60%) and fit mode
- **Custom titles**: the launcher's own window title (applies on focus loss) and the MC window title
  (uses the client's native `--title`)
- **Multiple running instances**: several games can run at once. While any is running, an
  **⏹ End game** button appears at the bottom right; clicking it opens a panel to pick which
  instance to end. Game log lines are prefixed with the instance name. Newly created instances
  show up in the home-page dropdown immediately.

### Internationalisation
- **Strings live in language packs**: `src/LPML.App/Locales/*.xaml`, zero hard-coded UI text
- XAML uses `{DynamicResource String.Module.Item}`; C# uses `Lang.T` / `Lang.F`; Core uses `L.T` / `L.F`
- **Switch at runtime**: Settings → Advanced → Language, applies instantly with no restart
- **Bundled**: Simplified Chinese (zh-CN) and English (en-US), 1530 entries each, keys 1:1
- Key format `String.<Module>.<Item>`, e.g. `String.Settings.Title`
- To add a language: copy `zh-CN.xaml`, translate the values, add one entry to `LauncherLanguage`

### Download Engine
- **Concurrent tasks** (1~128) + **segmented download** (HTTP Range, 8 segments, enabled above 2 MB)
- **Size-adaptive concurrency** (on by default) — Minecraft is mostly tiny files:
  ```
  30 KB library → 48      2 MB asset → 32      over 5 MB → 8
  ```
- **Gentle ramp-up**: starts at 4 and climbs every 240 ms, reaching full in ~1.7 s
- **Failure-rate adaptation** layered on top of the size baseline
- Resume (`.part` chunks), SHA-1 + size verification, single-thread fallback when Range is unsupported
- **403 / 404 auto-switch mirrors**, final fallback to official — retrying the same URL is pointless
- **429 backed off separately** with a plain-language message
- Speed limit, proxy, custom UA, optional "ignore certificate errors"

### Download Performance
- **HTTP/2 multiplexing + connection pooling**: `SocketsHttpHandler`, `MaxConnectionsPerServer=128`
- **DNS warm-up** before the run starts
- **Local library reuse (version dirs act as the cache)**: SHA-1 index of existing game directories,
  hard-linked on hit — zero copy, no extra disk use, no separate cache directory.
  Third-party launcher directories (HMCL, official) can be added as read-only reuse sources
- **Phase timing** and **bandwidth stats** (average / peak) to show whether the link is saturated

### Download Center
- **Install game**: version list → double-click to open the install page (name, loader, folder, assets)
- **Version channels**: Release / Snapshot (including pre-releases and release candidates) /
  April Fools / Legacy / All. The list no longer truncates at 400 entries, so early snapshots and
  joke builds remain searchable. April Fools builds are labelled `snapshot` in the official manifest
  and are classified separately here. Test and joke builds are not guaranteed to launch, and loaders
  may have no matching version; use a separate instance and folder
- **Modpack import**: local `.mrpack` / zip, plus online Modrinth packs with version and loader picking
- **Download mods**: search Modrinth and install into a chosen instance. Reachable from the Download
  Center tab and from any instance's Mods page; defaults to the instance selected on the home page,
  and the download item appears in the Task Center with its own progress

### Task Center
Top-right **Tasks** button, **visible only when there are tasks**. Three levels.
Task cards are typed: installing a game keeps the original card, while "Complete game files"
gets its own card listing the jar / libraries / assets being fetched (never an empty list).

```
Task list (one operation = one group)
  Install game            Downloading 82/312
  ▓▓▓▓▓░░░░
      ↓ click
Detail → Download: groups, each with its own progress bar
  📦 Modpack content     312/640 files   ▓▓▓▓▓░░░
  🎮 Vanilla game files   82/120 files   ▓▓▓▓▓▓░░
      ↓ click a group
  Per-file progress, percentage, speed and status
Detail → Install: click through to a flow chart of all steps
```

- **Right-click**: Retry / Redownload / Cancel, on both groups and single files
- Mods, modpacks, vanilla and Java downloads all land here
- Clear removes finished groups so they don't pile up

### Launch Flow
A progress window that **closes only once the game reaches the main menu**:

| Stage | What happens |
|---|---|
| ① Dependencies | Missing files, count, speed, ETA |
| ② Signing in | Microsoft / Yggdrasil / offline passes through |
| ③ Preparing | Builds JVM args and classpath |
| ④ Starting | Shows the last few MC log lines |
| ⑤ Closing | Closes on main-menu output (`Reloading ResourceManager`, `OpenAL initialized`, `Done`, ...) |

**Only one game at a time**: while the game is running, both the floating bottom-right button and the
launch button on the Play page become **⏹ Close game** — one click ends the game process (including
child processes). Any other launch entry point then shows "Game is running" instead of starting a
second process. If the launcher is closed and reopened while the game is still running, it detects
that game and keeps the same state. Closing it yourself is not reported as a crash.

### Instances & Isolation
Isolation data lives in **`.minecraft\instances\<id>\`**, so copying `.minecraft` moves everything.

| Mode | Version files | mods / saves / config | Traits |
|---|---|---|---|
| **User data only** (default) | `.minecraft`, shared | Instance dir | Saves space |
| **Full isolation** | Instance dir | Instance dir | Copy and go |

- Global: Settings → Paths; per instance: Instance Settings → Isolation ("follow global" available)
- One-click migration between modes; hard links on the same volume

### Instance Folders
Any number of folders, each a **complete game directory**. Absolute, relative and
environment-variable paths are all supported.
The **Instances → Folders** page is the single entry point: create / delete folders, set the current
one, and list the instances and versions actually on disk in that folder (scanned from real files,
not from the shared `.minecraft` cache). Creating a folder never reassigns existing instances.
Instance names are checked per folder, so two folders may hold instances with the same name.
Resolution order: explicit directory > instance folder > isolation dir > default `.minecraft`.
Each instance directory gets an **`lpml.json`** (name / version / loader / isolation mode) that travels with it.

### Java Management
- Lists JDK / JRE with type labels (`JDK 21 · Eclipse Temurin · 64-bit`)
- **Auto-retry with another Java on failure**: same version first, never downgrades
- Auto-detection (PATH / JAVA_HOME / common dirs / game runtime), official runtime download when missing

### Accounts
- **Microsoft**: device code flow, token refresh — **bring your own Azure Client ID**, none is bundled
- **Offline**: offline UUID per official rules, **also customisable** — position 15 (`3`) and 20 (`8/9/a/b`)
  are protocol-fixed and auto-corrected, the other 30 characters are free
- **External**: any Yggdrasil / authlib-injector server, javaagent injected at launch
- **LittleSkin**: built-in official API

### Built-in Documents
Settings → Documents — a two-level "list → content" view with **Chinese and English** versions:
User Guide, Project README, and Markdown syntax notes.

### Online Updates

The launcher fetches `update.json` and its signature from `https://lpml.pages.dev/api/v1/lpml/` over **HTTPS only**:

- The manifest must pass **Ed25519** verification with the built-in public key, and the package must match the **SHA-256** in the manifest; otherwise nothing is applied.
- Only a strictly higher `num` than the locally stored one counts as an update. The new `num` / `id` are saved locally; nothing is uploaded.
- `important`: `1` required (the dialog cannot be closed, Esc / Alt+F4 blocked), `2` important, `3` optional. Automatic checks can hide optional updates; a manual check still shows them.
- Both URLs in the manifest are tried. The dialog picks their order: manifest order (auto), GitHub direct first, or proxy first; the preferred one goes first and the other stays as a fallback.
- Packages come **with or without the .NET runtime**. By default the type follows the current install (detected from the program files); it can be overridden in Settings or the dialog. Switching to "without runtime" requires .NET 10 on the machine.
- Localised release notes point to JSON files whose `main` field holds Markdown; a missing note does not block a safe update.
- Applying: extract to `LPML\cache\update-*` → start the staged build → it replaces the program files and launches the new launcher → the staged copy is removed. Config, accounts, logs and `.minecraft\` game data are untouched; the replacement is logged to `LPML\logs\update.log` and rolls back from a backup on failure.
- **Settings → Online Update** holds the master switch, check-now, optional-update muting, route, package type, signature file name, and shows the current update number; "Check for updates on startup" is under Advanced. A failed check never blocks the launcher.
- Verification protects the manifest in transit only — it is **not a runtime authorisation**. The program itself is unsigned; turning updates off or changing the endpoint does not stop it running, and a required update is triggered by `num`, never by detecting a modified build.

#### Manifest format and self-hosting

`update.json` uses snake_case (full definition: `src/LPML.Core/Update/UpdateModels.cs`): `name` / `id` (required), `num` (positive, strictly increasing), `have` (`false` pauses delivery), `important` (1/2/3), `have_runtime_down` + `have_runtime_sha256` and `no_runtime_down` + `no_runtime_sha256` (arrays of two HTTPS package URLs and the matching hash), `update_name_zh` / `update_name_en`, `update_introduce_zh` / `update_introduce_en` (relative paths to JSON files whose `main` is Markdown), and an optional HTTPS `github` release page.

The signature is an **Ed25519 signature over the raw bytes of `update.json`**, placed beside it; common names such as `update.json.sig` are probed automatically, or set one in Settings. Raw 64-byte, base64 / hex text, PEM blocks and `{"signature":"..."}` are accepted; the public key is SPKI PEM (RFC 8410), with the corresponding raw 32-byte constant alongside it.

To host your own feed: generate an Ed25519 key pair, sign the raw manifest bytes, publish manifest + signature over HTTPS, then replace `BaseUrl`, `PublicKeyPem` and `PublicKeyRaw` in `src/LPML.Core/Update/UpdateService.cs` with your endpoint and key (all three must match the same key). A modified build runs the full update flow and is not blocked because it was modified; the official feed serves official binaries only. Do not add terms forbidding changes to the public key or update URL — that would conflict with the rights GPLv3 grants.

> Every binary release is paired with a same-version source release; see "Corresponding source" below.

### Log Guard Process (child process)
On startup the launcher spawns a second `--guard` instance of the same exe with `Process.Start`.
Because it is a direct child, Windows Task Manager groups both LPML entries together.

- **No lost startup logs**: until the guard is ready, the main process writes `lpml.log` itself.
  Once the guard signals READY, the main process flushes and switches its log sink to a named pipe;
  the switch happens under the log lock, so nothing is dropped or interleaved.
- After handover every entry (including multi-line stack traces) is written by the guard.
- **Fallback**: if the guard fails to start or disconnects, the main process switches back to its own file sink.
- **Watchdog**: the heartbeat is driven by the UI thread's `DispatcherTimer`, so a frozen UI stops it.
  After the threshold (default 20s) the guard offers to force-close the launcher; an option skips the prompt.
- The guard exits as soon as the host process exits, and loads no WPF resources at all.
- **Settings → Advanced**: enable guard / unresponsive threshold / force close without asking (requires restart).
- CLI: `--no-guard`, `--guard-timeout=30`, `--guard-auto-kill`.

### Multiplayer (Terracotta / EasyTier)

P2P multiplayer powered by [Terracotta](https://burningtnt.github.io/Terracotta/) and [EasyTier](https://easytier.cn/), working out of the box:

- **Default route**: clicking "Download core" fetches the official v0.4.2 bundle, CN mirrors first (Gitee → CNB → mirror → GitHub). Version metadata is read from the same `terracotta.json` HMCL uses; if unavailable, built-in templates and a verified SHA-512 take over.
- **Integration**: the unmodified official binary is started directly, without any private launch arguments. LPML never opens a browser itself and talks to the local HTTP API on `127.0.0.1` only. If Terracotta opens its own browser page, the same actions can be performed there. The OS-assigned port is discovered via `terracotta.lock` (2-byte big-endian), a port JSON file, or process output. All actions use the local HTTP API: `/state`, `/state/scanning`, `/state/guesting`, `/state/ide`, `/meta`, `/log`, `/panic?peaceful=true`.
- **HMCL compatible**: invitation codes use the same `U/XXXX-XXXX-XXXX-XXXX` format (base-34, no I/O), so HMCL / PCL CE / FCL codes work directly. You can also point Settings at HMCL's downloaded core folder to reuse the same binary.
- **Your own config is kept**: override URL, version and checksum (SHA-512, legacy SHA-256 accepted); v0.4.2 supports custom EasyTier public nodes (one per line, merged with built-ins); fixed invitation code and display name are optional.
- **Note**: P2P is less reliable than relayed servers and is meant for playing with friends, not as a replacement for a Minecraft Java Edition server. Cross-launcher connectivity depends on the other launcher's version and network and is not guaranteed.

> **Copyright**: Terracotta © Burning_TNT and contributors, licensed **AGPL-3.0-or-later (with exception)**; built on EasyTier. LPML does not link Terracotta code — it talks to the unmodified official binary over its local HTTP API and shows the copyright notice prominently in the multiplayer UI.

### Other
- Mod management: reads `fabric.mod.json` / `mods.toml` / `mcmod.info`, toggle (`.jar.disabled`), delete, add
- Modpack import: Modrinth `.mrpack`, HMCL / MultiMC / Curse exports, plus **Modrinth online search**
- Full launch-parameter control: memory, GC, JVM args, env vars, priority, window size, direct connect
- Integrity check and one-click repair; launch command preview
- **Command-line mode** (`--cli`) with an interactive REPL that forwards MC logs.
  First CLI run (including one-shot queries) shows the full user agreement; only `是` / `我同意` / `同意` / `yes` accepts it, any other input closes the CLI. The fixed bottom hint only mentions `help`; type `help` for the full command list (about 50 commands).
- Log archive: `lpml.log` is packed into `oldlog1.zip` at startup, older archives shift down, max 5 kept
- Numbered logs: `[#0042] [22:05:42] [Info ] [Java] ...`

---

## 🏗 Project Layout

```
LPML.sln
src/
├─ LPML.Core/            # Pure logic (net10.0, no third-party deps, unit-testable)
│  ├─ Core/ Config/ Mirror/ Download/ Game/ Java/ Auth/ Launch/
│  ├─ Instances/ Mods/ Integration/ Utils/
└─ LPML.App/             # WPF UI (net10.0-windows)
   ├─ Locales/           # Language packs: zh-CN.xaml / en-US.xaml (1530 entries each)
   ├─ Views/             # 19 pages (incl. task center, version install, modpack install, mod download, docs)
   ├─ Windows/ Utils/ Services/ Themes/ Styles/ Helpers/ Controls/
docs/                    # Built-in documents (zh-CN / en-US)
tools/static_check.py    # Static checker
```

---

## 🚀 Build

Requires **.NET 10 SDK** (with the Windows Desktop runtime), Windows 10 1809+ / Windows 11.

```powershell
dotnet run --project src\LPML.App

.\build.ps1                       # Release publish
.\build.ps1 -SelfContained $true  # Self-contained
.\build.ps1 -Configuration Debug
```

---

## ⚠️ Known Limitations

1. Microsoft login needs your own Azure Client ID — no third-party ID is bundled or managed
2. Forge / NeoForge install via the official installer, so a working Java is required
3. OptiFine's official installer is GUI-only; it's launched for you to click through
4. CurseForge modpack mods must be added manually (`manifest.json` carries no URLs)
5. Online updates require `https://lpml.pages.dev/api/v1/lpml/` to be reachable; an unverified manifest or a SHA-256 mismatch aborts the update. A level-1 update must be applied before continuing. The package type follows the current install (bundled .NET runtime or not) by default and can be switched in Settings or the update dialog.
6. BMCLAPI is occasionally flaky (403 / 429); switching happens automatically. Third-party loader maven artifacts (Fabric / Forge / Quilt / NeoForge) now go to their dedicated mirrors or the official source instead of BMCLAPI's /maven

---

## 📄 Licence and Disclaimer

**Licence**: this project is licensed under the **GNU General Public License, version 3 or (at your option) any later version** (SPDX: `GPL-3.0-or-later`). The full text is in [LICENSE](/LICENSE). You may copy, modify and redistribute this software and derivative works; when you convey it you must preserve copyright and licence notices, include a copy of the licence, and make the complete corresponding source available under the same licence.

**Copyright & attribution**: all copyright and contributor attributions for this project are collected in **[NOTICE](/NOTICE)** rather than repeated in every source file. GPLv3 section 4 requires an appropriate copyright notice on **each copy** of the work; the FSF *How to Apply These Terms* template likewise allows each file to carry only "a pointer to where the full notice is found" — per-file headers are described as "safest", not mandatory. A root `Directory.Build.props` writes the copyright line into the assembly metadata (visible under File Properties → Details on the compiled dll), and `NOTICE` + `LICENSE` shipped with every source and binary package constitute that full notice. If you convey a modified version, mark the changed files with your modifications and their dates as required by GPL section 5, and keep those files.

**Corresponding source**: every binary release is paired with a same-version source release; they correspond to each other and are both licensed under GPL-3.0-or-later, and that source package is the "complete corresponding source code" for the binary. The project repository is <https://github.com/yyy13245-dev/lpml>.

This licence covers LPML only. It does **not** cover the Minecraft client, mods, modpacks, Java runtimes, the Terracotta binary or other third-party content, each of which remains subject to its own terms (see below and [DISCLAIMER.md](/DISCLAIMER.md)).

**Disclaimer summary** (full text: [DISCLAIMER.md](/DISCLAIMER.md)):

- This is an unofficial third-party tool, unaffiliated with Mojang Studios / Microsoft / Modrinth / BMCLAPI; all trademarks belong to their respective owners.
- The software and all documentation were generated mainly with AI assistance and are provided "AS IS", without warranties of any kind.
- You assume all risks of downloading, installing and launching the game, mods and modpacks, signing in, making network requests and reading or writing files. Back up your worlds and configs yourself.
- To the maximum extent permitted by law, maintainers and contributors are not liable for any direct or indirect loss. Nothing here excludes rights that applicable law does not permit to be waived.

On first launch the *User Agreement and Disclaimer* (`docs/<language>/agreement.md`) is displayed and must be accepted before use: click **Agree and Continue** in the GUI, or type `是` / `我同意` / `同意` / `yes` in the CLI. It can be re-read at any time from the **Documents** page, or with the `agreement` command in the CLI.

---

## 🤝 Feedback and Contributions

Issues are welcome; please open one before submitting a PR to discuss the scope. This project is AI-assisted and maintained with limited resources, so response times are not guaranteed. Do not attach account tokens, logs, game files or third-party copyrighted assets to Issues or PRs.
