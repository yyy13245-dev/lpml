# 免责声明 · Disclaimer

> 本文件同时以中文和英文提供。两种语言仅为便于阅读，若理解不一致，以中文版为准；两种文本都不构成法律意见。
> This document is provided in both Chinese and English for convenience. If the two versions differ in interpretation, the Chinese version prevails. Neither version constitutes legal advice.

---

## 中文

**最后更新：版本 1**

### 1. 非官方、无关联

LPML（Let's Play Minecraft Launcher）是**第三方、非官方**的 Minecraft Java 版启动器。本项目的维护者与贡献者与 Mojang Studios、Microsoft、Minecraft 相关商标权利人、Modrinth、BMCLAPI、光速镜像以及任何模组、整合包、资源包作者或镜像节点运营者**均不存在隶属、代理、授权、赞助或背书关系**。

"Minecraft"、"Mojang"、"Microsoft"、"Forge"、"Fabric"、"Quilt"、"NeoForge"、"Modrinth"、"BMCLAPI" 等名称与商标归各自权利人所有。本仓库中对它们的引用仅用于说明兼容性与数据来源，不表示任何关联，也不授予任何商标或版权许可。

本软件**不分发** Minecraft 客户端、服务端、资源文件、账户凭据或任何第三方受版权保护的内容；运行时所需文件由用户自行从官方或第三方服务获取，是否获取、从何处获取由用户自行决定并承担相应责任。

### 2. AI 辅助生成，"现状"提供

本项目的源代码、文档、界面文案与内置说明**主要由 AI 辅助生成**，未经完整的人工审计、安全评估与系统化测试，可能包含缺陷、逻辑错误、遗漏、过时信息或不符合预期之处。

本软件按 **"现状"（AS IS）** 提供，不附带任何明示或默示的担保，包括但不限于：适销性、特定用途适用性、所有权、不侵权、无错误、不间断运行、安全性、兼容性，以及"会修复任何问题"或"会持续维护"的承诺。维护者不承诺发布更新、提供技术支持或维持任何服务可用。

### 3. 风险由用户自行承担

使用本软件即表示你理解并接受：下载、安装、启动游戏，安装模组、整合包、加载器、Java 运行时，修改或删除本地文件，发起网络请求，以及登录账户等**全部操作及其后果由你自行承担**，可能包括但不限于：

- 存档、配置、模组、实例数据或其他文件的损坏与丢失；
- 系统卡顿、崩溃、磁盘空间占用、网络流量费用、代理或防火墙告警；
- 账户令牌过期、登录失败、账号受限或第三方服务条款项下的其他后果；
- 从第三方获取的文件不完整、被篡改、携带恶意代码、侵犯他人权利或不符合你的预期；
- 因软件缺陷、误操作、断电、系统环境差异等导致的任何直接或间接损失。

**请在首次使用前自行备份重要数据**（存档、配置、账户信息、系统还原点等），并自行判断所下载内容的来源、完整性、许可证与安全性。维护者不承担备份、审查、恢复或赔偿义务。

### 4. 第三方内容与外部服务

**多人联机组件**：本软件的多人联机功能通过本地 HTTP API 调用**未经修改**的 Terracotta | 陶瓦联机官方二进制（© Burning_TNT 及贡献者，依 GNU Affero General Public License v3.0 or later 授权，并附例外条款），该程序基于 EasyTier。其版权、商标与许可归各自权利人所有，本软件不主张任何权利，也不静态或动态链接其代码；依据该例外条款，通过进程间通信接口与其交互不会使本软件被 AGPL 涵盖，本软件已在联机界面明显处标注版权信息。陶瓦联机依赖第三方公共节点与 P2P 穿透，可能失败、延迟或中断；跨启动器互通取决于第三方启动器的版本与网络环境，不受本软件控制，亦不作保证。


本软件可能向官方服务、镜像站、Modrinth 等第三方发起请求，并按用户选择下载第三方提供的文件。维护者**不控制、不审查、不担保**这些服务与文件，也不对其可用性、内容、准确性、合法性、安全性或持续性负责。第三方服务可能随时变更、限流、停止服务或修改条款；由此造成的一切影响由用户自行承担。

**在线更新**：启用时启动器会主动访问 `https://lpml.pages.dev/api/v1/lpml/` 拉取更新清单与签名，并在你确认后下载整包、替换程序目录下的文件并重启启动器。清单须通过 Ed25519 验签、整包须通过 SHA-256 校验；这些校验只保证文件在传输中未被篡改，**不构成对更新内容无缺陷、无恶意代码或适合你环境的担保**，更新包仍可能带来新的缺陷、兼容性问题或数据风险，请在更新前自行备份。在线更新可在「设置 → 在线更新」中关闭；关闭后或更换为自建端点后程序照常运行，验签不用于限制修改版的运行。维护者不为修改版提供更新、支持或分发。

用户应自行遵守 Minecraft 最终用户许可协议（EULA）、各第三方服务条款、所下载内容各自的许可证，以及所在地法律法规。不得将本软件用于侵犯他人知识产权、规避付费、共享或交易账户、攻击或干扰第三方服务等用途。

### 5. 账户与本地数据

本软件为便携设计，配置与账户数据默认保存在启动器目录内；微软登录等在线功能会按相应服务的要求与该服务直接通信。维护者**不收集、不接收、不托管**你的账户凭据、令牌、存档或日志，也不运营任何后台服务。

账户凭据与令牌由用户自行保管；共用电脑、将启动器目录整体复制或上传、把日志与配置发送给他人等行为带来的泄露风险，由用户自行承担。若怀疑凭据泄露，请立即在对应服务处撤销并更换。

### 6. 责任限制

在**适用法律允许的最大范围内**，维护者、贡献者及本项目的任何关联方，均不对因使用、无法使用、误用本软件或依赖其任何信息而产生的任何直接、间接、附带、特殊、惩罚性或后果性损失承担责任，包括但不限于数据丢失、利润或收入损失、设备或系统损坏、业务中断、账号受限、第三方索赔，即使已被告知此类损失可能发生。

上述限制**不排除适用法律明确规定不可放弃的权利或责任**（例如某些司法辖区对故意或重大过失、人身伤害的责任限制）。本声明的任何条款均不试图排除这类强制性规定；若某条款被认定无效，其余条款继续有效。

### 7. 赔偿

因你使用或误用本软件、违反第三方条款或适用法律、侵害他人权利，或你提供、下载、分发的内容而引发的索赔、调查、罚款、费用与损失，由你自行承担；你同意使维护者与贡献者免受由此产生的责任与费用（包括合理的律师费）。

### 8. 协议版本与同意

首次启动图形界面时会显示《用户协议与免责声明》，点击"同意并继续"即表示你已完整阅读、理解并接受该协议及本声明。协议版本更新后会再次显示；继续使用即视为接受更新后的版本。不同意者应停止使用本软件；这不影响你依 `LICENSE` 复制、修改或分发本软件的权利。

命令行模式（`--cli`）第一次启动时同样会完整显示该协议，只有输入「是 / 我同意 / 同意 / yes」之一才表示接受并继续使用；输入其他内容、直接回车或无法读取输入时，命令行会直接退出。在脚本或无人值守场景中使用本软件，同样视为已阅读并接受上述全部条款。

### 9. 其他

本软件依 **GNU General Public License v3.0 或任何更新版本**（`GPL-3.0-or-later`）发布，完整文本见随附的 `LICENSE` 文件。本声明与《用户协议与免责声明》仅约束**使用本软件的行为**及由此产生的风险分配，**不限制、不减损 GPL 授予你的复制、修改、分发与获取源代码的权利**，也不对行使这些权利附加额外条件。本软件的版权与贡献者署名集中记载于随附的 `NOTICE` 文件，分发时须一并保留。项目仓库地址：<https://github.com/yyy13245-dev/lpml>。本声明或用户协议与 `LICENSE` 理解不一致时，以 `LICENSE` 为准。

本声明不构成法律意见。如对条款有疑问，请自行咨询具备资质的专业人士。

---

## English

**Last updated: Version 1**

### 1. Unofficial and unaffiliated

LPML (Let's Play Minecraft Launcher) is a **third-party, unofficial** launcher for Minecraft Java Edition. The maintainers and contributors are **not affiliated with, authorised by, sponsored by, or endorsed by** Mojang Studios, Microsoft, holders of Minecraft-related trademarks, Modrinth, BMCLAPI, FastMinecraftMirror, or any author of mods, modpacks, resource packs, or mirror node operator.

Names and trademarks such as "Minecraft", "Mojang", "Microsoft", "Forge", "Fabric", "Quilt", "NeoForge", "Modrinth" and "BMCLAPI" belong to their respective owners. References to them in this repository describe compatibility and data sources only; they imply no association and grant no trademark or copyright licence.

This software **does not redistribute** the Minecraft client, server, assets, account credentials, or any third-party copyrighted content. Files required at runtime are obtained by the user from official or third-party services; the user decides whether and from where to obtain them and bears the resulting responsibility.

### 2. AI-assisted, provided "AS IS"

The source code, documentation, UI strings and bundled guides of this project were **generated mainly with AI assistance**. They have not undergone a complete human audit, security review or systematic testing, and may contain defects, errors, omissions, outdated information or unexpected behaviour.

The software is provided **"AS IS"**, without warranties of any kind, express or implied, including but not limited to merchantability, fitness for a particular purpose, title, non-infringement, error-free or uninterrupted operation, security, compatibility, or any commitment to fix issues or maintain the project. No update, support or continued availability is promised.

### 3. Risks assumed by the user

By using this software you understand and accept that **you bear all risks and consequences** of downloading, installing and launching the game; installing mods, modpacks, loaders and Java runtimes; creating, modifying or deleting local files; making network requests; and signing in to accounts. These may include, without limitation:

- corruption or loss of worlds, configurations, mods, instance data or other files;
- system slowdowns or crashes, disk usage, network charges, proxy or firewall alerts;
- expired tokens, failed logins, account restrictions or other consequences under third-party terms;
- files obtained from third parties being incomplete, modified, malicious, infringing, or otherwise unsuitable;
- any direct or indirect loss caused by defects, misoperation, power loss or differences in system environment.

**Back up important data before first use** (worlds, configs, account information, system restore points) and assess the source, integrity, licence and safety of anything you download yourself. The maintainers have no duty to back up, review, restore or compensate.

### 4. Third-party content and external services

**Multiplayer components**: the multiplayer feature talks over a local HTTP API to the **unmodified** official Terracotta binary (© Burning_TNT and contributors, GNU Affero General Public License v3.0 or later with an exception), which builds on EasyTier. All copyrights, trademarks and licences remain with their respective owners. LPML claims no rights and does not statically or dynamically link their code; under that exception, interacting through the inter-process interface does not bring LPML under the AGPL, and the copyright notice is displayed prominently in the multiplayer UI. Terracotta depends on third-party public nodes and P2P traversal, which may fail, lag or drop. Cross-launcher interoperability depends on the third-party launcher's version and network conditions; it is outside our control and is not guaranteed.


The software may contact official services, mirrors, Modrinth and other third parties, and downloads files chosen by the user. The maintainers **do not control, review or warrant** those services or files, nor their availability, content, accuracy, legality, security or continuity. Third-party services may change, rate-limit, shut down or amend their terms at any time; all resulting effects are borne by the user.

**Online updates**: when enabled, the launcher contacts `https://lpml.pages.dev/api/v1/lpml/` for a manifest and signature and, after your confirmation, downloads a package that replaces files in the program directory and restarts the launcher. The manifest must pass Ed25519 verification and the package must match its SHA-256; these checks only show that the files were not altered in transit and **are not a warranty that the update is defect-free, free of malicious code or suitable for your environment**. An update may still introduce new defects, compatibility problems or data risks — back up before updating. Online updates can be switched off under Settings → Online Update; turning them off or pointing at your own endpoint does not stop the program from running. Signature verification is not used to restrict modified builds, and the maintainers provide no updates, support or distribution for modified versions.

You are responsible for complying with the Minecraft EULA, the terms of each third-party service, the licences of everything you download, and the laws of your jurisdiction. This software must not be used to infringe intellectual property, bypass payment, share or trade accounts, or attack or disrupt third-party services.

### 5. Accounts and local data

The software is portable: configuration and account data are stored in the launcher directory by default. Online features such as Microsoft sign-in communicate directly with the corresponding service as that service requires. The maintainers **do not collect, receive or host** your credentials, tokens, worlds or logs, and operate no backend service.

You are responsible for safeguarding credentials and tokens. Sharing a computer, copying or uploading the launcher folder, or sending logs and configs to others creates disclosure risks that you bear alone. If you suspect a leak, revoke and rotate the credential at the relevant service immediately.

### 6. Limitation of liability

**To the maximum extent permitted by applicable law**, the maintainers, contributors and any associated party are not liable for any direct, indirect, incidental, special, punitive or consequential loss arising from the use, inability to use, misuse of, or reliance on this software, including but not limited to data loss, lost profits or revenue, device or system damage, business interruption, account restrictions and third-party claims, even if advised of the possibility of such loss.

These limitations **do not exclude rights or liabilities that applicable law does not permit to be waived** (for example, in some jurisdictions, liability for intent, gross negligence or personal injury). Nothing here attempts to exclude such mandatory rules; if any provision is held invalid, the remainder stays in effect.

### 7. Indemnity

Claims, investigations, fines, costs and losses arising from your use or misuse of this software, your breach of third-party terms or applicable law, infringement of others' rights, or content you provide, download or distribute are yours to bear. You agree to hold the maintainers and contributors harmless from such liability and costs, including reasonable legal fees.

### 8. Agreement version and consent

On first GUI launch a *User Agreement & Disclaimer* is displayed; clicking **Agree and Continue** means you have read, understood and accepted that agreement and this disclaimer. An updated version is shown again when the agreement version changes; continued use constitutes acceptance of the updated terms. If you do not agree, stop using the software; this does not affect your rights under `LICENSE` to copy, modify or redistribute it.

On the first command-line (`--cli`) run the agreement is likewise displayed in full, and only typing 是 / 我同意 / 同意 / yes accepts it and lets you continue; any other input, a blank line, or unreadable input closes the CLI. Using the software in scripted or unattended scenarios likewise constitutes acceptance of all the terms above.

### 9. Miscellaneous

This software is released under the **GNU General Public License, version 3 or (at your option) any later version** (`GPL-3.0-or-later`); the full text is in the accompanying `LICENSE` file. This disclaimer and the *User Agreement & Disclaimer* govern **the act of using this software** and the allocation of risk only. They **do not limit or reduce your rights under the GPL** to copy, modify, redistribute and obtain the source code, and impose no additional conditions on exercising those rights. Copyright and contributor attributions are collected in the accompanying `NOTICE` file, which must be preserved when conveying the software. The project repository is <https://github.com/yyy13245-dev/lpml>. In the event of a conflict, `LICENSE` prevails.

Neither version of this disclaimer constitutes legal advice. Consult a qualified professional if you have questions.
