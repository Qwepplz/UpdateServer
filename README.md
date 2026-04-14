# UpdateServer

`UpdateServer` 是一个 Windows 更新器，用于把 `Qwepplz/pug` 与 `Qwepplz/get5` 同步到 `UpdateServer.exe` 所在目录。程序直接通过 HTTPS 读取仓库元数据与文件内容，因此目标机器不需要安装 Git。

## Contents

| Language | Link |
| --- | --- |
| English | [English Guide](#english-guide) |
| 中文 | [中文说明](#中文说明) |

## English Guide

### Overview

| Item | Description |
| --- | --- |
| Goal | Sync `pug`, `get5`, or both into the folder that contains `UpdateServer.exe`. |
| Platform | Windows. |
| Git requirement | None on the target machine. |
| Upstream content | `Qwepplz/pug` and `Qwepplz/get5`. |
| Source policy | The updater can read from GitHub and Gitee mirror endpoints. They are treated as equivalent mirrors of the same content. Concrete access order is documented in `docs/maintenance.md`, not in this README. |
| Safety model | The updater verifies downloaded files, confines writes to the target folder, and only deletes files that it previously tracked. |

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
| `1` / numpad `1` | Sync `pug`. |
| `2` / numpad `2` | Sync `get5`. |
| `3` / numpad `3` | Sync both repositories in sequence. |
| `ESC` | Exit immediately without syncing. |
| Input unavailable | If key input cannot be read, the updater falls back to syncing both repositories. |

### Stable Behavior

| Area | Behavior |
| --- | --- |
| Transport | Reads repository metadata and files directly over HTTPS. |
| Mirror semantics | GitHub and Gitee are treated as mirrors of the same content, not as different upstreams. |
| Root documents | Root `README*` and `LICENSE*` style files are not handled like ordinary synced files. |
| File deletion | Deletes only files that were previously tracked by this updater and are now gone upstream. |
| Protected targets | Protects the running updater, known local helper files, and existing log files from overwrite or deletion. |
| State isolation | Keeps separate state for each target folder and for each repository. |
| Logging | Writes daily log files into `log\` beside `UpdateServer.exe` when logging is available. |

### Build
| Item | Description |
| --- | --- |
| Default command | `tools\Build-UpdateServer.bat` |
| No-pause command | `tools\Build-UpdateServer.bat --no-pause` |
| Compiler model | The build script uses the Windows .NET Framework `csc.exe` toolchain when available. |
| Default output | `dist\UpdateServer.exe` |

### Repository Layout

Only files that currently exist in this repository are listed below.

| Path | Description |
| --- | --- |
| `README.md` | Stable user-facing project guide. |
| `docs\maintenance.md` | Implementation-level notes that may change more often than the README. |
| `LICENSE` | License file. |
| `src\UpdateServer\Program.cs` | Single-file C# implementation containing menu, sync, network, safety, cache, and logging logic. |
| `tools\Build-UpdateServer.bat` | Windows build script for `dist\UpdateServer.exe`. |

### Runtime-generated Paths

These paths are created locally during build or sync and are not tracked as repository files.

| Path | Description |
| --- | --- |
| `dist\UpdateServer.exe` | Default local build output produced by `tools\Build-UpdateServer.bat`. |
| `log\` | Runtime log directory created beside `UpdateServer.exe`. |
| `<state-root>\<target-hash>\pug\` | Per-target cache and state directory for `pug`. |
| `<state-root>\<target-hash>\get5\` | Per-target cache and state directory for `get5`. |

### Maintenance Notes

| Document | Purpose |
| --- | --- |
| `docs\maintenance.md` | Holds implementation details that may change more often, including the current source order, sync stages, cache/state details, and special-case file rules. |

This README intentionally stays focused on stable usage and repository facts so that routine maintenance work does not require frequent README edits.

### Basic Troubleshooting

| Symptom | Suggestion |
| --- | --- |
| `C# compiler was not found.` | Install or enable a .NET Framework 4.x compiler on Windows. |
| Sync failed | Open the current day's file in `log\`. |
| Another sync is already running | Close the other updater instance for the same target folder and retry. |

## 中文说明

### 项目定位

| 项目 | 说明 |
| --- | --- |
| 程序目标 | 将 `pug`、`get5` 或两者同步到 `UpdateServer.exe` 所在目录。 |
| 目标平台 | Windows。 |
| 目标机器依赖 | 不需要安装 Git。 |
| 上游内容 | `Qwepplz/pug` 与 `Qwepplz/get5`。 |
| 源策略 | 程序可从 GitHub 与 Gitee 镜像入口读取数据，两者被视为同一内容的镜像源。具体访问顺序属于实现细节，放在 `docs/maintenance.md` 中维护，而不是写死在 README。 |
| 安全模型 | 下载文件会校验，写入范围被限制在目标目录内，并且只会删除此前由本程序跟踪过的文件。 |
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
| `1` / 小键盘 `1` | 同步 `pug`。 |
| `2` / 小键盘 `2` | 同步 `get5`。 |
| `3` / 小键盘 `3` | 依次同步两个仓库。 |
| `ESC` | 立即退出，不执行同步。 |
| 无法读取输入 | 如果按键读取失败，程序会回退为同步全部仓库。 |

### 稳定行为说明

| 领域 | 行为 |
| --- | --- |
| 传输方式 | 直接通过 HTTPS 读取仓库元数据与文件内容。 |
| 镜像语义 | GitHub 与 Gitee 被视为同一内容的镜像入口，而不是不同上游。 |
| 根目录文档 | 根目录 `README*`、`LICENSE*` 一类文件不会按普通同步文件粗暴处理。 |
| 删除范围 | 只删除此前由本更新器记录过、且当前已从上游消失的文件。 |
| 受保护目标 | 会保护当前运行的更新器、已知本地辅助文件以及已有日志文件，不允许覆盖或删除。 |
| 状态隔离 | 会按目标目录与仓库分别保存独立状态。 |
| 日志 | 在 `UpdateServer.exe` 同级的 `log\` 目录按天写入日志。 |

### 构建

| 项目 | 说明 |
| --- | --- |
| 默认命令 | `tools\Build-UpdateServer.bat` |
| 跳过暂停 | `tools\Build-UpdateServer.bat --no-pause` |
| 编译方式 | 构建脚本在可用时使用 Windows .NET Framework 的 `csc.exe` 工具链。 |
| 默认输出 | `dist\UpdateServer.exe` |

### 仓库结构

这里只列出当前仓库中实际存在的文件。

| 路径 | 说明 |
| --- | --- |
| `README.md` | 面向使用者的长期稳定说明文档。 |
| `docs\maintenance.md` | 面向维护者的实现细节说明，允许随实现演进而调整。 |
| `LICENSE` | 许可证文件。 |
| `src\UpdateServer\Program.cs` | 单文件 C# 主实现，包含菜单、同步、网络、安全、缓存与日志逻辑。 |
| `tools\Build-UpdateServer.bat` | 用于生成 `dist\UpdateServer.exe` 的 Windows 构建脚本。 |

### 运行时生成路径
以下路径会在本地构建或同步时生成，不属于仓库已提交文件。

| 路径 | 说明 |
| --- | --- |
| `dist\UpdateServer.exe` | `tools\Build-UpdateServer.bat` 生成的默认本地产物。 |
| `log\` | 运行时在 `UpdateServer.exe` 同级创建的日志目录。 |
| `<状态根目录>\<目标目录哈希>\pug\` | `pug` 的按目标目录隔离缓存/状态目录。 |
| `<状态根目录>\<目标目录哈希>\get5\` | `get5` 的按目标目录隔离缓存/状态目录。 |

### 维护说明入口

| 文档 | 用途 |
| --- | --- |
| `docs\maintenance.md` | 存放更容易变化的实现细节，例如当前源顺序、同步阶段、缓存/状态细节，以及特殊文件处理规则。 |

本 README 刻意只保留稳定的使用说明与仓库事实，避免以后每次维护实现细节时都必须同步改 README。

### 基础排障

| 现象 | 建议 |
| --- | --- |
| `C# compiler was not found.` | 在 Windows 上安装或启用 .NET Framework 4.x 编译器。 |
| 同步失败 | 打开 `log\` 目录中的当日日志。 |
| 已有同步实例运行中 | 关闭同一目标目录下的另一个更新器实例后重试。 |
