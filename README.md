# UTSLManager

**UnifierTSL / TShock 多世界服务端管理器**（Windows 桌面客户端，.NET 10 + Avalonia 12）

面向 **NAT 后的服务器 + 机房 DPI 屏蔽 HTTP** 这类环境：**控制面走 SSH，数据面走 SSH 隧道**，不需要在服务器上暴露面板端口。

> 配套服务端插件仓库：[Zykor-Club/UTSLServerPlugin](https://github.com/Zykor-Club/UTSLServerPlugin)（WorldCommandChannel / NetPacketChannel / RuYouMusic / GenerateMap 等）
> 想给管理器加功能？见 [PLUGINS.md](PLUGINS.md)（插件 API v3，独立工程或 `.csx` 脚本，丢进 `plugins\\` 即生效）

---

## 功能

| 分类 | 能力 |
|---|---|
| 世界与指令 | 世界列表、按世界/按序号执行指令（`@Dev /save`、`@2 /save`、不写前缀=全部世界）、终端与服务器日志双面板 |
| 进程 | 启停/重启、状态三灯（隧道 / 命令通道 / 接口）、**看门狗**（掉线自启 + 连续 3 次熔断 + 启动/换图保护窗口） |
| 远端管理 | SSH 远端启停（WMI + 控制台启动器）、远端日志跟随、SSH 隧道自管（20 秒保活、断线自愈） |
| 文件 | 浏览（表格式）、下载 / 上传 / 删除、插件 `.dll` 一键启用停用、**传输进度与取消** |
| 配置 | 启动器 `config.json` 与 `EdgeStitch.json` 结构化编辑、保存自动 `.bak`、原子写 |
| 地图 | 地图预览（默认**缩略图**秒开，可「载入原图」看全尺寸）、缩放/适应/1:1 |
| 监控 | 控制台仪表盘：CPU（**单核 %**）/ 内存（**私有**）/ 网络，**监控的是远端服务器本身**（不是管理器这台机器）。装了服务端 `MetricsChannel` 插件时经现有 REST 隧道 **2 秒级实时**（实测单次 98~147 ms）；否则自动回退到每 45 秒的 SSH 合并快照（无插件也能用）。CPU/网卡速率均为**滚动 5 秒窗口**，多个调用方互不干扰 |
| 备份 | 远端打包 `Worlds`+`config` 并 **scp 下载到本机**形成离机副本 |
| 其它 | 世界链可视化、**自检入口**、插件系统（含插件管理页）、凭据 DPAPI 加密 |

---

## 架构

```
┌─────────────────────────┐         SSH（控制面：启停/日志/文件/配置）
│  UTSLManager (本机)      │ ───────────────────────────────────────────┐
│  Avalonia 桌面客户端      │                                            ▼
│                         │         SSH 隧道 -L 17878 → 远端 127.0.0.1:7878
│  · 合并快照（一次 ssh）   │ ───────────────────────────────────►  TShock/UTSL REST
│  · Fs* 文件抽象          │                                        （世界列表 / 指令 / 广播 / 存档）
│  · 90s 缓存 + 启动预热    │
└─────────────────────────┘         scp（大文件：地图 / 备份 / 上传下载）
```

**设计要点**
- 服务器 REST **只监听回环**，通过 SSH 隧道访问 → 公网不暴露任何面板端口。
- 远端命令一律 `ssh ... powershell -NoProfile -EncodedCommand <base64>`，规避 Windows 引号/转义问题。
- 一次 `RefreshSnapshot()` 取回「进程数 / 监听端口 / 启动器配置 / 世界链配置 / 根目录 + 常用子目录 / 地图列表」并写入缓存 → 各页面读缓存，**毫秒级**。

---

## 构建与发布

### 环境要求

- Windows 10/11（**必须 Windows**：桌面 UI + `user32`/`crypt32` P/Invoke + DPAPI）
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- 系统自带 OpenSSH 客户端（`ssh` / `scp`，用于远端模式）

### 本地构建

```powershell
git clone https://github.com/Zykor-Club/UTSL-Manager.git
cd UTSL-Manager

# 1) 发布管理器（⚠️ 只 build 不会部署，必须 publish）
dotnet publish -c Release
#   默认输出到 仓库上两级\AvaloniaUTSL\（与 csproj 里 PublishDir 一致）
#   想换位置：dotnet publish -c Release -p:PublishDir=D:\somewhere\

# 2) 构建官方扩展插件（会自动部署到 <管理器目录>\plugins\）
dotnet build extensions/PacketInspector/PacketInspector.csproj -c Release
dotnet build extensions/RuYouMusicPanel/RuYouMusicPanel.csproj -c Release
dotnet build extensions/ApiProbe/ApiProbe.csproj -c Release
#   管理器不在默认目录时：加 -p:ManagerDir=D:\path\to\AvaloniaUTSL

# 3) 运行
.\..\..\AvaloniaUTSL\UTSLManager.exe
```

> 扩展插件是**独立工程**，构建时会读取管理器发布目录里的 `UTSLManager.dll`（`-p:ManagerDir` 指定）；
> 管理器由 `.gitignore` 忽略的 `bin/`、`obj/`、`publish/` 不会进仓库。

### CI 工作流（GitHub Actions）

| 工作流 | 触发 | 做什么 |
|---|---|---|
| [`.github/workflows/build.yml`](.github/workflows/build.yml) | push / PR 到 `main`、手动 | `windows-latest` + .NET 10 → restore → build → publish → 构建三个官方扩展 → 上传产物 `UTSLManager-win-x64` |
| [`.github/workflows/release.yml`](.github/workflows/release.yml) | 打 `v*` 标签、手动 | 同上发布 + 打包 `UTSLManager-win-x64.zip` + 创建 GitHub Release（自动生成变更说明） |

**发版流程**

```powershell
git tag v1.0.0
git push origin v1.0.0     # 推送标签后，release 工作流自动产出 Release 与 zip
```

> CI 里统一用 `-p:PublishDir=${{ github.workspace }}/publish/` 覆盖发布目录，
> 并用 `-p:ManagerDir=…/publish` 让扩展插件找到管理器 DLL。

### 首次运行（连接设置）

首次运行后在 **连接设置** 页填写：REST 主机/端口与**令牌**、SSH 主机/端口/用户/密钥、远端 UTSL 目录、远端 REST 端口，点「保存并应用」→ 配置文件 `connection.json` 就在 exe 同目录：

```jsonc
{
  "host": "127.0.0.1", "port": 17878,          // 本机隧道入口
  "language": "zh-CN",
  "tokenEnc": "<DPAPI 加密后的令牌>",            // 不写明文；换机器/换用户需重新填
  "ssh": {
    "host": "<你的服务器>", "port": 22, "user": "<用户>",
    "key": "<私钥路径>", "dir": "C:\\utsl-deploy",
    "dotnet": "C:\\dotnet\\dotnet.exe", "launcher": "C:\\utsl-launch\\utsl-launch.dll",
    "restPort": 7878
  },
  "paths": { "localDir": "<本机 utsl-deploy 路径，本机模式用>" }
}
```

> **远端准备**：UTSL 运行目录就位；另外需要一个「控制台启动器」（`utsl-launch.dll`）—— UTSL 启动时要用真实控制台，纯 SSH 会话里 `Console.WindowHeight` 会抛「句柄无效」，启动器会用 `AllocConsole + CREATE_NEW_CONSOLE` 补一个。

---

## 自检（推荐）

```powershell
.\AvaloniaUTSL\UTSLManager.exe --selfcheck
# 结果写到 exe 同目录 selfcheck.txt
```

会逐项打印**真实耗时**：连接配置 / 隧道端口 / REST 世界列表 / 合并快照 / config / 根目录 / 地图 / 远端服务端 / 扩展 / **文件传输往返** / **地图原图全量** / DPAPI / 凭据存储 / 备份。

---

## 真实性能数据（本机实测，非估算）

| 项目 | 数据 |
|---|---|
| ssh 单次握手 | **1.6 s**（并发堆积到 40+ 个进程时会劣化到 **14~18 s**） |
| 单次远端读取（文件/配置） | 3~8 s |
| scp 下载 / 上传 | **1.31 MB/s** / **2.39 MB/s** |
| 换加密算法（aes128-gcm/ctr、chacha20）/ 压缩 / 2~4 流并发 | **均无提升**（瓶颈是链路） |
| base64 走 ssh（常驻通道口径） | 0.30~0.37 MB/s（比 scp **慢 4 倍**） |
| **合并快照**（唯一一次 ssh，每 45 s 后台） | 6.3~9.1 s |
| 各页面读取（快照命中后） | config **0~1 ms**、根目录 **0~3 ms**、地图 **0~5 ms**、服务端 **0 ms** |
| REST 世界列表（走隧道） | 275~336 ms |
| 地图缩略图 | 9.54 MB → **0.69 MB**；生成 5134 ms；下载 7064 ms → **1961 ms** |
| 备份 | 打包 7,339,267 B / **7.2 s** + 下载 7 MB / **5.8 s**（92 条目） |
| 看门狗 | 杀掉远端进程后 **40 s 自动拉起** |
| 失焦节流 | 前台 **5%** → 最小化 **1%** CPU |
| 传输往返校验 | 上传 64 KB → 远端 64 KB → 下载**逐字节一致** → 删除**已清除** |

> 场景：3 个世界（4200×2400）、远端 1 个服务端进程 + 3 个 ConsoleClient、REST 只在隧道内。

---

## 已知限制

- **远端读取不是瞬时的**：每次 ssh 都有握手成本（实测 1.6 s 起）。管理器的应对是「合并快照 + 90 s 缓存 + 启动预热 + 先显示后刷新」，所以页面体感是毫秒级，但**首次冷启动**仍会有几秒。
- **上传没有真实百分比**：`scp` 不回传进度，只显示大小与已用时间（不伪造进度条）。
- **地图首次**生成缩略图约 5 s（已用启动预热规避；之后走缓存）。
- **常驻命令通道（ssh 上挂 PowerShell 循环）已停用**：实测远端 PowerShell 在 ssh 循环里读 stdin 不可靠（第一次命令正常、之后收不到或空转刷屏），收益不值风险。
- 需要远端是 **Windows + OpenSSH**（脚本走 PowerShell）。

---

## 目录结构

```
├─ Program.cs                  入口（含 --selfcheck）
├─ App.axaml(.cs)              Avalonia 应用
├─ MainWindow.axaml(.cs)       主窗口：视图、远端访问（Fs*/SshRun/Snapshot）、REST、看门狗
├─ Plugins/                    管理器插件系统（API v2：IManagerPlugin / IManagerContext / PluginHost…）
├─ extensions/                 官方扩展插件源码（数据包查看器、点歌机面板、API 探针）
└─ PLUGINS.md                  插件开发指南
```

---

## 许可证

本项目以 **GNU General Public License v3.0**（GPL-3.0）发布，全文见 [LICENSE](LICENSE)。

```
Copyright (C) 2026 Zykor-Club

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
```

> GPL-3.0 是**强 copyleft**：分发本程序（含修改版）时必须提供完整源码并沿用同一许可。
> 若你只是自己架服自用，不受分发条款约束。
