# UpdateServer

Version: `v1.0.0`

## English

### Overview

`UpdateServer` is a Windows updater for `Qwepplz/pug` and `Qwepplz/get5`. Place `dist\UpdateServer.exe` in the target folder, launch it, and choose `1` for pug, `2` for get5, or `3` for both. The program first reads and downloads from the mirrored Gitee repositories `SaUrrr/pug` and `SaUrrr/get5`, then falls back to the GitHub repositories `Qwepplz/pug` and `Qwepplz/get5` if needed.

### Features

- Syncs files from the default branch of `Qwepplz/pug`, `Qwepplz/get5`, or both into the folder that contains `UpdateServer.exe`.
- Shows a startup menu: `1` = pug, `2` = get5, `3` = all.
- Falls back to common branch names such as `main` and `master` if default-branch lookup fails.
- Uses Gitee mirrors (`SaUrrr/pug`, `SaUrrr/get5`) as the primary source and GitHub (`Qwepplz/pug`, `Qwepplz/get5`) as the fallback source for repository metadata and file downloads.
- Overwrites local files only when content has changed, and skips unchanged files.
- Removes only files that were previously tracked by this updater and later deleted upstream.
- Excludes root-level `README*`, `LICENSE*`, `LICENCE*`, and `LECENSE*` files from sync. If a local copy exactly matches upstream and is safe to touch, the old synced copy may be removed.
- Always skips `addons/sourcemod/scripting/include/logdebug.inc` and `addons/sourcemod/scripting/include/restorecvars.inc` in all modes because they differ between `pug` and `get5`, and are only needed when compiling plugins.
- Protects the updater itself and known helper files so it does not overwrite its own tooling.
- Uses isolated cache data per target directory and per repository, plus a mutex to prevent concurrent syncs for the same folder.
- Writes updated files atomically to reduce the risk of corruption during replacement.
- Keeps download/update progress compact by refreshing two status lines, with the progress bar on its own full-width line instead of printing one new console line for every file.
- Mirrors console output into a `log` folder beside `UpdateServer.exe`, writes one timestamped file per day such as `log\UpdateServer-2026-04-12.log`, and keeps appending to that day's file.
- Does not run shell commands, modify registry startup entries, or create scheduled tasks.

### Repository Layout

| Path | Description |
| --- | --- |
| `src\UpdateServer\Program.cs` | Current main implementation: single-file C# updater source. |
| `tools\Build-UpdateServer.bat` | Build script that produces the local `dist\UpdateServer.exe` output and optionally signs it. |
| `dist\UpdateServer.exe` | Local build output path for the compiled executable. |
| `log\UpdateServer-YYYY-MM-DD.log` | Runtime log file written in the target folder's `log` directory, one file per day. |

### Build

Build on Windows with:

```bat
tools\Build-UpdateServer.bat
```

The script first looks for `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` and falls back to the `Framework` compiler if needed.

To skip the final pause:

```bat
tools\Build-UpdateServer.bat --no-pause
```

Default output:

```text
dist\UpdateServer.exe
```

### Optional Code Signing

If you have a real `.pfx` code-signing certificate, the EXE can be signed automatically after the build. The script looks for `signtool.exe` in `PATH` or common Windows SDK locations.

```bat
tools\Build-UpdateServer.bat "C:\path\code-signing.pfx" "password"
```

```bat
tools\Build-UpdateServer.bat --no-pause "C:\path\code-signing.pfx" "password"
```

Unsigned builds can still run, but because the program reads repository metadata over the network and updates local files, antivirus products or SmartScreen may still show heuristic warnings.

### Usage

1. Copy `dist\UpdateServer.exe` into the folder you want to sync.
2. Launch the program and confirm that the displayed target folder is correct.
3. Press `1` to sync pug (`Qwepplz/pug`), `2` to sync get5 (`Qwepplz/get5`), `3` to sync both, or `ESC` to exit.
4. Wait for the four stages to finish: reading the repository tree, handling root README/LICENSE files, downloading updates, and removing upstream-deleted files.
5. If you need to troubleshoot a run, open today's file in the `log` folder beside the updater.

### Cache and State

Sync state is stored in the first available location below:

1. `PUG_GET5_SYNC_STATE`
2. `%LOCALAPPDATA%\PugGet5Sync`
3. `%APPDATA%\PugGet5Sync`
4. `%TEMP%\PugGet5Sync`

Each target folder gets its own hashed subdirectory, and each repository gets its own nested state folder. Common state files include `tracked-files.txt` and `sync-state.json`.

### Safety Notes

- The updater refuses to touch paths outside the target folder.
- If a target path is actually a directory, the updater stops instead of overwriting it.
- Reparse-point paths are rejected to reduce the risk of accidentally touching linked locations.
- Temporary download files and replacement backups are cleaned up as much as possible after success or failure.

### Verify a Release File

```bat
certutil -hashfile dist\UpdateServer.exe SHA256
```

You can use the resulting SHA-256 digest for release verification or pre-distribution checks.

## 中文

### 概览

`UpdateServer` 是一个面向 Windows 的 `Qwepplz/pug` 和 `Qwepplz/get5` 更新器。将 `dist\UpdateServer.exe` 放到目标目录后运行，选择 `1` 同步 pug、`2` 同步 get5、`3` 同步全部。程序会优先从 Gitee 镜像仓库 `SaUrrr/pug` 与 `SaUrrr/get5` 读取仓库树并下载文件；如果失败，再回退到 GitHub 官方仓库 `Qwepplz/pug` 与 `Qwepplz/get5`，全程不依赖 Git。

### 功能特性

- 可将 `Qwepplz/pug`、`Qwepplz/get5` 或两者默认分支中的文件同步到 `UpdateServer.exe` 所在目录。
- 启动后显示选择菜单：`1` = pug，`2` = get5，`3` = all。
- 如果默认分支查询失败，会回退到常见分支名，例如 `main` 和 `master`。
- 默认优先使用 Gitee 镜像仓库（`SaUrrr/pug`、`SaUrrr/get5`）；当镜像不可用时，再回退到 GitHub 官方仓库（`Qwepplz/pug`、`Qwepplz/get5`）读取仓库元数据并下载文件。
- 只有在内容发生变化时才会覆盖本地文件；未变化文件会直接跳过。
- 只会删除此前由本更新器跟踪、且后来已被上游删除的文件。
- 根目录中的 `README*`、`LICENSE*`、`LICENCE*`、`LECENSE*` 文件默认不参与同步；如果本地副本与上游完全一致且可安全处理，旧的同步副本可能会被移除。
- `addons/sourcemod/scripting/include/logdebug.inc` 和 `addons/sourcemod/scripting/include/restorecvars.inc` 在所有模式下都会始终跳过，因为它们在 `pug` 与 `get5` 中内容不同，而且只在编译插件时才需要。
- 会保护更新器自身和已知辅助文件，避免覆盖自己的工具文件。
- 每个目标目录、每个仓库都会使用独立缓存，并通过互斥锁防止同一目录同时运行多个同步任务。
- 更新文件时采用原子写入方式，以降低替换过程中损坏文件的风险。
- 下载/更新阶段的文件级进度会以两行控制台状态原地刷新，其中进度条单独占满一整行，避免每个文件都新增一行输出。
- 会把控制台输出同时写入 `UpdateServer.exe` 同目录下的 `log` 文件夹，并按日期生成日志文件（例如 `log\UpdateServer-2026-04-12.log`）；每行都会带时间戳，并持续追加到当天文件。
- 程序不会执行 shell 命令、不会修改注册表启动项，也不会创建计划任务。

### 仓库结构

| 路径 | 说明 |
| --- | --- |
| `src\UpdateServer\Program.cs` | 当前主实现：使用 C# 编写的单文件更新器源码。 |
| `tools\Build-UpdateServer.bat` | 构建脚本，可生成本地 `dist\UpdateServer.exe` 输出，也支持可选签名。 |
| `dist\UpdateServer.exe` | 编译后 EXE 的本地输出路径。 |
| `log\UpdateServer-YYYY-MM-DD.log` | 运行时日志文件，写在目标目录下的 `log` 文件夹中，并按天分文件。 |

### 构建

在 Windows 上构建：

```bat
tools\Build-UpdateServer.bat
```

脚本会先查找 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，如果不存在，则回退到 `Framework` 目录下的编译器。

如需跳过构建结束后的暂停：

```bat
tools\Build-UpdateServer.bat --no-pause
```

默认输出：

```text
dist\UpdateServer.exe
```

### 可选代码签名

如果你有真实可用的 `.pfx` 代码签名证书，可以在构建后自动为 EXE 签名。脚本会在 `PATH` 或常见的 Windows SDK 安装目录中查找 `signtool.exe`。

```bat
tools\Build-UpdateServer.bat "C:\path\code-signing.pfx" "password"
```

```bat
tools\Build-UpdateServer.bat --no-pause "C:\path\code-signing.pfx" "password"
```

即使是不带签名的构建也可以运行；但由于程序会联网读取仓库元数据并更新本地文件，杀毒软件或 SmartScreen 仍可能给出启发式警告。

### 使用方法

1. 将 `dist\UpdateServer.exe` 复制到需要同步的目标目录。
2. 启动程序，并确认显示的目标目录正确无误。
3. 按 `1` 同步 pug（`Qwepplz/pug`），按 `2` 同步 get5（`Qwepplz/get5`），按 `3` 同步全部，或按 `ESC` 退出。
4. 等待程序完成四个阶段：读取仓库树、处理根目录 README/LICENSE 文件、下载更新文件，以及删除上游已移除文件。
5. 如果需要排查运行问题，请打开与更新器同目录下 `log` 文件夹中的当日日志。

### 缓存与状态

同步状态会写入以下第一个可用位置：

1. `PUG_GET5_SYNC_STATE`
2. `%LOCALAPPDATA%\PugGet5Sync`
3. `%APPDATA%\PugGet5Sync`
4. `%TEMP%\PugGet5Sync`

每个目标目录都会生成独立的哈希子目录，每个仓库也会使用独立的嵌套状态目录；常见状态文件包括 `tracked-files.txt` 和 `sync-state.json`。

### 安全说明

- 程序拒绝处理目标目录之外的路径。
- 如果目标路径实际上是目录，程序会停止，而不是覆盖它。
- 程序会拒绝处理重解析点路径，以降低误操作链接位置的风险。
- 临时下载文件和替换备份文件会在成功或失败后尽量清理。

### 校验发布文件

```bat
certutil -hashfile dist\UpdateServer.exe SHA256
```

你可以将得到的 SHA-256 摘要用于发布校验或分发前自检。
