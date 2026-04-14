# UpdateServer

Version: `v1.0.0`

`UpdateServer` is a Windows updater that syncs files from `Qwepplz/pug` and `Qwepplz/get5` into the folder that contains `UpdateServer.exe`. The target machine does not need Git; the updater reads repository metadata and file contents directly over HTTPS.

## Contents

| Language | Link |
| --- | --- |
| English | [English Guide](#english-guide) |
| 中文 | [中文说明](#中文说明) |

## English Guide

### Purpose

| Item | Description |
| --- | --- |
| Goal | Sync `pug`, `get5`, or both into the folder where `UpdateServer.exe` is running. |
| Platform | Windows. |
| Git requirement | None on the target machine. |
| Upstream repositories | `Qwepplz/pug` and `Qwepplz/get5`. |
| Access order | Gitee mirrors first, matching GitHub repositories second. |
| Order meaning | The two sources are treated as equivalent entrances for the same content; the order is only about network convenience, not content priority. |

### Quick Start

| Step | Action |
| --- | --- |
| 1 | Build with `tools\Build-UpdateServer.bat`. |
| 2 | Get the output file at `dist\UpdateServer.exe`. |
| 3 | Copy `dist\UpdateServer.exe` into the folder you want to sync. |
| 4 | Run `UpdateServer.exe`. |
| 5 | Press `1` for `pug`, `2` for `get5`, `3` for both, or `ESC` to exit. |
| 6 | If a run fails, inspect `log\UpdateServer-YYYY-MM-DD.log` beside the updater. |

### Startup Menu

| Key | Action |
| --- | --- |
| `1` / numpad `1` | Sync `Qwepplz/pug`. |
| `2` / numpad `2` | Sync `Qwepplz/get5`. |
| `3` / numpad `3` | Sync both repositories in sequence. |
| `ESC` | Exit immediately without syncing. |
| Input unavailable | If key input cannot be read, the updater falls back to syncing both repositories. |

### Sync Sources and Network Strategy

| Data | First source | Second source |
| --- | --- | --- |
| `pug` repo metadata | `https://gitee.com/api/v5/repos/SaUrrr/pug` | `https://api.github.com/repos/Qwepplz/pug` |
| `get5` repo metadata | `https://gitee.com/api/v5/repos/SaUrrr/get5` | `https://api.github.com/repos/Qwepplz/get5` |
| Repository tree | Gitee Git Tree API | GitHub Git Tree API |
| Raw file download | `https://gitee.com/<owner>/<repo>/raw/<branch>/<path>` | `https://raw.githubusercontent.com/<owner>/<repo>/<branch>/<path>` |

- The updater first queries the default branch of the selected repository.
- If default-branch lookup fails, it still tries common branch names such as `main` and `master`.
- Repository trees must be complete. If a remote API returns a truncated tree, sync is aborted because deletion would no longer be safe.
- The sync logic does not use `gh.sevencdn.com` as an API or raw-content source.
- Network requests use TLS 1.2 and a fixed timeout.

### Sync Flow

| Stage | Console label | Description |
| --- | --- | --- |
| 1 | `[1/4] Reading repository tree...` | Reads the branch and the full remote file tree. |
| 2 | `[2/4] Removing repo README/LICENSE when safe...` | Handles root README/LICENSE files specially so local docs are not blindly overwritten. |
| 3 | `[3/4] Downloading and updating files...` | Adds missing files, updates changed files, and skips unchanged files through cache and hash checks. |
| 4 | `[4/4] Removing files deleted upstream...` | Deletes only files that were previously tracked by this updater and are now gone upstream. |

### File Rules

| File type | Behavior |
| --- | --- |
| Normal tracked file | Added if missing, updated if changed, skipped if unchanged. |
| Root `README*`, `LICENSE*`, `LICENCE*`, `LECENSE*` | Not treated as normal sync files; an old synced copy may be removed only when it exactly matches upstream and is safe to touch. |
| `addons/sourcemod/scripting/include/logdebug.inc` | Always skipped because it is a compile-only conflict file. |
| `addons/sourcemod/scripting/include/restorecvars.inc` | Always skipped because it is a compile-only conflict file. |
| File deleted upstream | Removed only if it appears in the updater's previously tracked manifest/state. |
| Updater executable and helper files | Protected from overwrite and deletion. |
| Existing log files | Protected from sync operations. |

### Safety Guarantees

| Area | Behavior |
| --- | --- |
| Target boundary | Refuses to touch paths outside the selected target folder. |
| Reparse points | Refuses to touch paths that pass through a reparse point. |
| Directory conflicts | Stops if a target file path is actually an existing directory. |
| Concurrent runs | Uses a named mutex per target folder to prevent simultaneous syncs into the same location. |
| File verification | Verifies each downloaded file against the Git blob SHA-1 from the remote tree. |
| Replacement mode | Uses staging files plus `File.Replace` / `File.Move` for atomic-style replacement. |
| Cleanup | Removes stale `.__pug_get5_sync_staging__*` and `.__pug_get5_sync_backup__*` files on startup when safe. |
| System behavior | Does not run shell commands, modify registry startup entries, or create scheduled tasks. |

Protected local targets include:

| Type | Description |
| --- | --- |
| Running executable | The updater protects the actual executable path it is currently running from. |
| Existing local helper files | The updater also protects known local helper files if they already exist in the target folder. |
| Existing log files | Files already inside the target folder's `log` directory are protected. |

### Cache, State, and Logs

| Item | Description |
| --- | --- |
| State root priority | `PUG_GET5_SYNC_STATE` → `%LOCALAPPDATA%\PugGet5Sync` → `%APPDATA%\PugGet5Sync` → `%TEMP%\PugGet5Sync` |
| Target isolation | The updater hashes the target folder path with SHA-256 and stores state under that hash. |
| Repository isolation | `pug` and `get5` each use their own nested state directory. |
| Legacy manifest | `tracked-files.txt` |
| Current state file | `sync-state.json` |
| Cached metadata | Remote SHA, local file length, and last-write timestamp |
| Log directory | `log` beside `UpdateServer.exe` |
| Log naming | One file per day, such as `log\UpdateServer-2026-04-14.log` |

### Repository Layout

Only files that currently exist in this repository are listed below.

| Path | Description |
| --- | --- |
| `README.md` | Project documentation. |
| `LICENSE` | License file. |
| `src\UpdateServer\Program.cs` | Single-file C# implementation containing the menu, sync logic, network access, safety checks, progress display, cache handling, and logging. |
| `tools\Build-UpdateServer.bat` | Windows build script for `dist\UpdateServer.exe`, with optional code signing. |

### Runtime-generated Paths

These paths are created locally during build or sync and are not tracked as repository files.

| Path | Description |
| --- | --- |
| `dist\UpdateServer.exe` | Default local build output produced by `tools\Build-UpdateServer.bat`. |
| `log\` | Runtime log directory created beside `UpdateServer.exe` when logging is available. |
| `<state-root>\<target-hash>\pug\` | Per-target cache/state directory for `pug`. |
| `<state-root>\<target-hash>\get5\` | Per-target cache/state directory for `get5`. |

### Build

Build on Windows with:

```bat
tools\Build-UpdateServer.bat
```

Skip the final pause with:

```bat
tools\Build-UpdateServer.bat --no-pause
```

The script first looks for:

```text
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

If that compiler is not available, it falls back to:

```text
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
```

Build references:

| Reference | Purpose |
| --- | --- |
| `System.Web.Extensions.dll` | `JavaScriptSerializer` JSON parsing |
| `System.Core.dll` | LINQ and related base functionality |

Default output:

```text
dist\UpdateServer.exe
```

### Optional Code Signing

If you have a real `.pfx` code-signing certificate, the build script can sign the generated EXE automatically:

```bat
tools\Build-UpdateServer.bat "C:\path\code-signing.pfx" "password"
```

Or together with `--no-pause`:

```bat
tools\Build-UpdateServer.bat --no-pause "C:\path\code-signing.pfx" "password"
```

The script searches `PATH` and common Windows SDK locations for `signtool.exe`.

### Verify a Build Artifact

```bat
certutil -hashfile dist\UpdateServer.exe SHA256
```

Use the resulting SHA-256 digest for release verification or pre-distribution checks.

### Troubleshooting

| Symptom | Suggestion |
| --- | --- |
| `C# compiler was not found.` | Install or enable a .NET Framework 4.x compiler on Windows. |
| `Another Pug/Get5 sync is already running for this folder.` | Close the other updater instance for the same target folder and retry. |
| `Repository API returned a truncated tree.` | Retry later or from another network; sync stops intentionally to protect deletion safety. |
| `Refusing to touch a path outside the target folder` | Check target-folder selection, path mappings, or unexpected remote paths. |
| `Refusing to touch a reparse point path` | Remove or replace junctions / symlinks / reparse points in the target path and retry. |
| Output is not enough to diagnose a failure | Open the current day's file in `log\`. |

## 中文说明

### 项目定位

| 项目 | 说明 |
| --- | --- |
| 程序目标 | 将 `pug`、`get5` 或两者同步到 `UpdateServer.exe` 所在目录。 |
| 目标平台 | Windows。 |
| 目标机器依赖 | 不需要安装 Git。 |
| 当前上游仓库 | `Qwepplz/pug` 与 `Qwepplz/get5`。 |
| 默认访问顺序 | 先访问 Gitee 同步镜像，再访问 GitHub 对应仓库。 |
| 顺序含义 | 两组地址被视为内容应保持一致的同步入口；先后顺序只用于访问便利，不表示主备或内容优先级。 |

### 快速开始

| 步骤 | 操作 |
| --- | --- |
| 1 | 在仓库根目录运行 `tools\Build-UpdateServer.bat`。 |
| 2 | 构建成功后得到 `dist\UpdateServer.exe`。 |
| 3 | 将 `dist\UpdateServer.exe` 复制到需要同步的目标目录。 |
| 4 | 运行 `UpdateServer.exe`。 |
| 5 | 按 `1` 同步 `pug`，按 `2` 同步 `get5`，按 `3` 同步全部，或按 `ESC` 退出。 |
| 6 | 如果同步失败，请查看更新器旁边的 `log\UpdateServer-YYYY-MM-DD.log`。 |

### 启动菜单

| 按键 | 行为 |
| --- | --- |
| `1` / 小键盘 `1` | 同步 `Qwepplz/pug`。 |
| `2` / 小键盘 `2` | 同步 `Qwepplz/get5`。 |
| `3` / 小键盘 `3` | 依次同步两个仓库。 |
| `ESC` | 立即退出，不执行同步。 |
| 无法读取输入 | 如果按键读取失败，程序会回退为同步全部仓库。 |

### 同步源与网络策略

| 数据 | 第一访问入口 | 第二访问入口 |
| --- | --- | --- |
| `pug` 仓库信息 | `https://gitee.com/api/v5/repos/SaUrrr/pug` | `https://api.github.com/repos/Qwepplz/pug` |
| `get5` 仓库信息 | `https://gitee.com/api/v5/repos/SaUrrr/get5` | `https://api.github.com/repos/Qwepplz/get5` |
| 仓库树 | Gitee Git Tree API | GitHub Git Tree API |
| 原始文件下载 | `https://gitee.com/<owner>/<repo>/raw/<branch>/<path>` | `https://raw.githubusercontent.com/<owner>/<repo>/<branch>/<path>` |

- 程序会先查询所选仓库的默认分支。
- 如果默认分支查询失败，还会继续尝试常见分支名 `main` 与 `master`。
- 仓库树必须完整返回；如果远端 API 返回的是截断树，程序会终止同步，因为那样删除逻辑将不再安全。
- 同步逻辑不会把 `gh.sevencdn.com` 当作 API 或 Raw 文件来源。
- 网络请求使用 TLS 1.2，并带固定超时。

### 同步流程

| 阶段 | 控制台标识 | 说明 |
| --- | --- | --- |
| 1 | `[1/4] Reading repository tree...` | 读取目标分支与完整远端文件树。 |
| 2 | `[2/4] Removing repo README/LICENSE when safe...` | 特殊处理根目录 README/LICENSE 文件，避免粗暴覆盖本地说明文档。 |
| 3 | `[3/4] Downloading and updating files...` | 新增缺失文件、更新已变化文件，并通过缓存与哈希校验跳过未变化文件。 |
| 4 | `[4/4] Removing files deleted upstream...` | 只删除此前由本更新器跟踪、且当前已从上游移除的文件。 |

### 文件处理规则

| 文件类型 | 处理方式 |
| --- | --- |
| 普通跟踪文件 | 不存在则新增，内容变化则更新，未变化则跳过。 |
| 根目录 `README*`、`LICENSE*`、`LICENCE*`、`LECENSE*` | 不按普通同步文件处理；只有在本地副本与上游完全一致且安全可触碰时，旧同步副本才可能被移除。 |
| `addons/sourcemod/scripting/include/logdebug.inc` | 始终跳过，因为它是编译期冲突文件。 |
| `addons/sourcemod/scripting/include/restorecvars.inc` | 始终跳过，因为它是编译期冲突文件。 |
| 上游已删除文件 | 仅当该文件出现在此前的清单/状态记录里时才会删除。 |
| 更新器自身与辅助文件 | 受保护，不会被覆盖或删除。 |
| 现有日志文件 | 受保护，不参与同步操作。 |

### 安全保证

| 领域 | 行为 |
| --- | --- |
| 目标目录边界 | 拒绝触碰目标目录之外的路径。 |
| 重解析点 | 拒绝触碰经过 reparse point 的路径。 |
| 目录冲突 | 如果目标文件路径实际是一个已有目录，程序会停止。 |
| 并发运行 | 针对每个目标目录使用命名互斥锁，避免同时写入同一位置。 |
| 文件校验 | 每个下载文件都会用远端仓库树提供的 Git blob SHA-1 做校验。 |
| 替换方式 | 使用 staging 临时文件加 `File.Replace` / `File.Move` 进行原子式替换。 |
| 启动清理 | 启动时会在安全前提下清理残留的 `.__pug_get5_sync_staging__*` 与 `.__pug_get5_sync_backup__*` 文件。 |
| 系统行为 | 不会执行 shell 命令、不会修改注册表启动项、不会创建计划任务。 |

受保护的本地目标包括：

| 类型 | 说明 |
| --- | --- |
| 当前运行的可执行文件 | 程序会保护自己当前实际运行的 EXE 路径。 |
| 已存在的本地辅助文件 | 如果目标目录里已经存在已知辅助文件，程序也会保护它们。 |
| 已存在的日志文件 | 目标目录 `log` 目录内已有的日志文件会被保护。 |

### 缓存、状态与日志

| 项目 | 说明 |
| --- | --- |
| 状态根目录优先级 | `PUG_GET5_SYNC_STATE` → `%LOCALAPPDATA%\PugGet5Sync` → `%APPDATA%\PugGet5Sync` → `%TEMP%\PugGet5Sync` |
| 目标目录隔离 | 程序会对目标目录路径做 SHA-256 哈希，并将状态存放到对应子目录。 |
| 仓库隔离 | `pug` 与 `get5` 各自使用独立的嵌套状态目录。 |
| 旧清单文件 | `tracked-files.txt` |
| 当前状态文件 | `sync-state.json` |
| 缓存内容 | 远端 SHA、本地长度、本地最后写入时间 |
| 日志目录 | `UpdateServer.exe` 同级的 `log` 目录 |
| 日志命名 | 按天生成一个文件，例如 `log\UpdateServer-2026-04-14.log` |

### 仓库结构

这里只列出当前仓库中实际存在的文件。

| 路径 | 说明 |
| --- | --- |
| `README.md` | 项目说明文档。 |
| `LICENSE` | 许可证文件。 |
| `src\UpdateServer\Program.cs` | 单文件 C# 主实现，包含菜单、同步、网络访问、安全校验、进度显示、缓存处理和日志逻辑。 |
| `tools\Build-UpdateServer.bat` | Windows 构建脚本，用于生成 `dist\UpdateServer.exe`，并支持可选代码签名。 |

### 运行时生成路径

以下路径是在本地构建或同步时生成的，不属于仓库已提交文件。

| 路径 | 说明 |
| --- | --- |
| `dist\UpdateServer.exe` | `tools\Build-UpdateServer.bat` 生成的默认本地构建产物。 |
| `log\` | 运行时在 `UpdateServer.exe` 同级创建的日志目录。 |
| `<状态根目录>\<目标目录哈希>\pug\` | `pug` 的按目标目录隔离缓存/状态目录。 |
| `<状态根目录>\<目标目录哈希>\get5\` | `get5` 的按目标目录隔离缓存/状态目录。 |

### 构建

在 Windows 上构建：

```bat
tools\Build-UpdateServer.bat
```

如需跳过最后的暂停：

```bat
tools\Build-UpdateServer.bat --no-pause
```

脚本会优先查找：

```text
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

若不存在，则回退到：

```text
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
```

构建引用：

| 引用 | 用途 |
| --- | --- |
| `System.Web.Extensions.dll` | 使用 `JavaScriptSerializer` 解析 JSON |
| `System.Core.dll` | LINQ 等基础能力 |

默认输出：

```text
dist\UpdateServer.exe
```

### 可选代码签名

如果你有真实可用的 `.pfx` 代码签名证书，构建脚本可以在生成 EXE 后自动签名：

```bat
tools\Build-UpdateServer.bat "C:\path\code-signing.pfx" "password"
```

也可以与 `--no-pause` 组合使用：

```bat
tools\Build-UpdateServer.bat --no-pause "C:\path\code-signing.pfx" "password"
```

脚本会在 `PATH` 与常见 Windows SDK 安装路径中查找 `signtool.exe`。

### 校验构建产物

```bat
certutil -hashfile dist\UpdateServer.exe SHA256
```

得到的 SHA-256 摘要可用于发布校验或分发前自检。

### 常见问题

| 现象 | 建议 |
| --- | --- |
| `C# compiler was not found.` | 在 Windows 上安装或启用 .NET Framework 4.x 编译器。 |
| `Another Pug/Get5 sync is already running for this folder.` | 关闭同一目标目录下的另一个更新器实例后重试。 |
| `Repository API returned a truncated tree.` | 稍后重试或切换网络；程序主动停止是为了保护删除安全。 |
| `Refusing to touch a path outside the target folder` | 检查目标目录选择、路径映射或异常远端路径。 |
| `Refusing to touch a reparse point path` | 去掉或替换目标路径中的 junction / symlink / reparse point 后再试。 |
| 控制台信息不足以定位错误 | 打开 `log` 目录中的当日日志文件。 |
