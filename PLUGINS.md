# UTSLManager 插件开发指南（API **v6**）

> 想让管理器长出新功能，又不想改管理器源码？写一个**管理器插件**。
> 插件是独立工程（或一个 `.csx` / `.lua` 脚本），丢进 `plugins\` 即可，管理器启动时自动加载。

**适用**：UTSLManager (Avalonia) · .NET 10 · Avalonia 12.1.2 · 插件 API v6　　　**最后更新**：2026-10-17

## 目录

| 想干什么 | 看这里 |
|:--|:--|
| 5 分钟跑通一个插件 | §1 快速开始 |
| 知道自己该用哪种扩展方式 | **§2 扩展点决策表** |
| 查某个 API 的签名/语义 | **§3 API 速查** |
| 多台服务器下插件要不要改 | **§3.6 多档（servers.json）** |
| 查 manifest 字段 / 权限 | §4 清单 · §5 权限 |
| 写界面、配色 | §7 写界面 |
| 用脚本（不想建工程） | §8 脚本插件（.csx / .lua） |
| 找现成例子抄 | **§9 示例索引** |
| 踩坑 / 报错 | §10 硬性规则 · §11 FAQ |

---

## 0. 先分清两种插件

|  | **管理器插件**（本文档） | 服务端插件 |
|:--|:--|:--|
| 跑在哪 | UTSLManager 进程里 | UTSL 服务端进程里 |
| 放哪 | `AvaloniaUTSL\plugins\` | `utsl-deploy\plugins\` |
| 用什么 API | `IManagerPlugin` / `IManagerContext`（Avalonia UI） | TShock / UTSL API |
| 能干什么 | 加界面、订阅管理器事件、加终端命令/指标、调度 REST、读写部署目录、定时启停服务端 | 改服务端玩法、发指令、封包处理 |

两者互不相干：右侧「📁 文件管理」管的是**服务端插件**；管理器插件在「🧩 插件管理」页与右侧「扩展」分组。

---

## 1. 30 秒上手

### 1.1 最小 C# 插件（完整可跑）

~~~csharp
using System.Threading.Tasks;
using Avalonia.Controls;
using UTSLManager.Plugins;

public sealed class MyPlugin : IManagerPlugin
{
    public string Name        => "我的插件";
    public string Icon        => "✨";
    public string Description => "一句话说明";
    public string Author      => "你的名字";

    // 用到哪个版本的能力就写哪个；写 ManagerApi.Version 表示要最新（宿主太旧会被拒绝加载并给出提示）
    public int RequiredApiVersion => ManagerApi.Version;

    public void OnLoad(IManagerContext ctx)
    {
        ctx.Log("[我的插件] 已加载");
        ctx.RegisterCommand("hi", "打个招呼", args =>            // 终端里 !hi 参数
        {
            args.Reply("hi " + args.RawArguments);
            return Task.CompletedTask;
        });
        ctx.RegisterMetric("我的插件 · 调用", "次", () => 0);      // 右侧「插件指标」区
        ctx.RegisterStatus("我的插件 · 状态", () => "就绪");
        ctx.Events.ServerStateChanged += up => ctx.Log("[我的插件] 服务端 " + (up ? "上线" : "下线"));
    }

    public void OnUnload() { /* 保存状态 / 停后台任务 */ }

    public Control? CreateView(IManagerContext ctx) => new TextBlock { Text = "你好，管理器！", FontSize = 16 };
}
~~~

要求：**public、非抽象、有无参构造函数**的类。一个文件里可以有多个实现（会全部加载），用 `manifest.entry` 可只加载指定的那一个。

### 1.2 csproj（可直接复制）

~~~xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>        <!-- 必须与管理器一致 -->
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>MyPlugin</AssemblyName>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>   <!-- ★ 只输出插件自己的 dll -->
    <ManagerDir Condition="$(ManagerDir) == ''">C:\Users\你的用户名\Desktop\AvaloniaUTSL</ManagerDir>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="12.1.2" />   <!-- 版本必须与宿主一致 -->

    <!-- ★ 插件只需引用【契约程序集】（约 28KB）。命名空间仍是 UTSLManager.Plugins，using 不用改。
         Private=false 必须要有：运行时用管理器已加载的那一份；
         复制出第二份会让类型标识不一致，管理器认不出你的插件（而且不一定报错）。 -->
    <Reference Include="UTSLManager.Plugin.Abstractions">
      <HintPath>$(ManagerDir)\UTSLManager.Plugin.Abstractions.dll</HintPath>
      <Private>false</Private>
    </Reference>

    <!-- 只有要调宿主的地图/渲染公开 API（UTSLManager.Maps.*）时才再引管理器本体（参考 extensions\MapTemplates）。
         老插件 dll 无需重编 —— 管理器里保留了 TypeForwardedTo。
    <Reference Include="UTSLManager">
      <HintPath>$(ManagerDir)\UTSLManager.dll</HintPath>
      <Private>false</Private>
    </Reference>
    -->
  </ItemGroup>

  <!-- 构建后自动部署（管理器在跑时文件被占用 → 只警告、不让 build 失败） -->
  <Target Name="DeployToManager" AfterTargets="Build">
    <MakeDir Directories="$(ManagerDir)\plugins" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(ManagerDir)\plugins" ContinueOnError="true" Retries="0" />
  </Target>
</Project>
~~~

### 1.3 构建 · 部署 · 验证

~~~powershell
dotnet build -c Release                                    # 会自动复制到 <ManagerDir>\plugins\
dotnet build -c Release -p:ManagerDir="D:\path\to\AvaloniaUTSL"   # 管理器不在默认目录时
~~~

没配自动部署就手动把 `MyPlugin.dll`（+ 可选 `MyPlugin.manifest.json`）放进 `<管理器目录>\plugins\`，然后重启管理器或点「🧩 插件管理 → 重新加载」。

**成功的三个信号**：① 右侧「扩展」出现你的按钮；②「🧩 插件管理」页出现插件名/版本/API/权限；③ 终端打印 `[插件] 已加载 ✨ 我的插件 v1.0.0（MyPlugin.dll，作者 …）`。

### 1.4 不想建工程？两种脚本

| 方式 | 文件 | 特点 |
|:--|:--|:--|
| C# 脚本 | `plugins\xxx.csx` | Roslyn 编译，能用完整 C# + Avalonia 界面；见 §8.1 |
| Lua 脚本 | `plugins\<名>\plugin.lua` | 不编译、改完即热重载；能力见 §8.2 |

---

## 2. 我该用哪个扩展点

**先看这张表，不用翻源码。**

| 想做的事 | 用哪个 | 备注 |
|:--|:--|:--|
| 加一整个功能页（中间区域） | `IManagerPlugin.CreateView` | 最常用；返回 `null` = 纯后台插件 |
| 后台干活、不加界面 | `IManagerPlugin` + `CreateView => null` | 用 `RunBackground` 起循环 |
| 加终端命令 `!名字` | `ctx.RegisterCommand` | 内置 `!help` 会列出所有插件命令 |
| 右侧显示数值 / 文本状态 | `ctx.RegisterMetric` / `RegisterStatus` | 每 2 秒回调一次，**必须快** |
| 监听上下线 / 切世界 / 终端行 | `ctx.Events` | 见 §3.3 |
| 读写部署目录（本机/远端统一） | `ctx.ListFiles` / `ReadText` / `WriteText` … | 需 `fs`；远端走 SSH/scp |
| 调 UTSL REST | `ctx.RestGet` / `RestPost` | 需 `rest`；自动带令牌 |
| 在部署主机执行命令 | `ctx.RunCommand` | 需 `exec`，**能力很强** |
| **启停服务端进程** | `ctx.StartServerAsync` / `StopServerAsync` | 需 `exec`；走管理器按钮**同一条**路径 |
| **往「地图预览」页挂按钮** | `IMapContributionPlugin` | 见 §3.5 |
| **往「文件管理」页加右键动作** | `IFileActionPlugin` | 见 §3.5；示范 `extensions\ApiProbe` |
| **要定时任务 / 不想写 C#** | Lua 插件 | 见 §8.2；现成范例 `extensions\LuaTaskScheduler` |

---

## 3. API 速查

命名空间 `UTSLManager.Plugins`（类型在 **`UTSLManager.Plugin.Abstractions.dll`** 里；管理器侧有类型转发，老插件不受影响）。

### 3.1 IManagerPlugin

| 成员 | 类型 | 说明 |
|:--|:--|:--|
| `Name` | `string` | 显示名（右侧菜单 + 管理页） |
| `Icon` | `string` | 菜单图标，emoji 即可 |
| `Description` | `string` | 一句话说明 |
| `Author` | `string` | 作者；为空时回退 manifest |
| `RequiredApiVersion` | `int` | 要求的 API 版本，默认 1；写 `ManagerApi.Version` 表示要最新 |
| `OnLoad(IManagerContext)` | `void` | 加载时调用一次；抛异常只禁用本插件 |
| `OnUnload()` | `void` | 卸载/重载/关闭时调用（默认空实现） |
| `CreateView(IManagerContext)` | `Control?` | 中间区域界面；纯后台插件返回 `null`（默认） |

`ManagerApi.Version` 当前是 **6**；常量 `V1` / `V3` / `V5`（文件页动作）/ `V6`（服务端进程控制）可用于声明最低要求。

### 3.2 IManagerContext（一张表看全）

| 分类 | 成员 | 说明 | 权限 |
|:--|:--|:--|:--|
| 身份 | `PluginName` / `PluginDir` / `DataDir` | 插件名 / 所在目录 / 专属数据目录（自动创建）。**`DataDir` 随激活档变**，见 §3.6 | — |
| 连接 | `DeployRoot` / `RestBase` / `Token` / `Language` | 部署根 / REST 基地址 / 令牌 / 界面语言（**前三个随激活档变**，都是惰性回调） | — |
| 权限 | `Permissions` | 声明的权限集合（未声明 = 空集合 = **全部允许**） | — |
| 日志 | `Log(string)` | 往管理器终端写一行（按级别着色） | — |
| REST | `RestGet(url)` / `RestPost(url)` | 访问当前连接的 UTSL REST，自动带令牌 | `rest` |
| 世界 | `Worlds` | **当前激活档**的世界列表快照（`WorldInfo{ Name, Size, Running }`） | — |
| 事件 | `Events` | 事件总线，见 §3.3 | — |
| 生命周期 | `ShutdownToken` / `RunBackground(work, name?)` | 关闭令牌 / 后台任务（异常写终端，关闭时自动取消） | — |
| 命令 | `RegisterCommand(name, desc, handler)` / `UnregisterCommand(name)` | 终端 `!name 参数` 触发 | — |
| 指标 | `RegisterMetric(name, unit, provider)` / `UnregisterMetric` | 数字，右侧「插件指标」区 | — |
| 状态 | `RegisterStatus(name, provider)` / `UnregisterStatus` | 文本状态胶囊 | — |
| 配置 | `GetConfig<T>()` / `SaveConfig<T>(v)` | `<DataDir>\config.json`（带 `fileName` 参数可多文件） | `config` |
| 文本 | `ReadDeployText(rel)` / `WriteDeployText(rel, text)` | 老 API；路径限制在部署目录内 | `fs` |
| 文件 | `FilesRoot` / `ListFiles(dir)` / `FileExists(p)` / `ReadText(p)` / `WriteText(p,t)` / `CreateDirectory(d)` / `Rename(p,newName)` / `Delete(p)` / `Upload(local,rel)` / `Download(rel,local)` / `InvalidateFileCache()` | **统一部署目录访问**：远端走 SSH/scp，本机直接读盘 | `fs` |
| 命令执行 | `RunCommand(powerShell, timeoutMs=30000)` | 在部署主机执行 PowerShell（远端走 ssh） | `exec` |
| HTTP | `HttpGet(url)` / `HttpPost(url, jsonBody?)` | 任意 http(s)，15 秒超时 | `net` |
| 服务端 | `ServerOnline` | 是否在线（管理器**心跳探测**结果，本机/远端同一语义） | — |
| 服务端 | `StartServerAsync(reason?)` / `StopServerAsync(reason?)` | 启停服务端进程；`reason` 会写进终端便于审计 | `exec` |
| i18n | `RegisterStrings(lang, dict)` / `L(key, fallback?)` | 取串顺序：当前语言 → 任一语言 → fallback → key | — |

**文件 API 三个要点**：① 相对路径一律相对 `FilesRoot` 且**强制留在其内部**（`../..` 会抛 `IOException: 路径越界`，已实测拦截）；② 远端调用有成本（ssh 每次约 1.5~8 秒）→ 批量场景请缓存或合并；③ 改过文件后调 `InvalidateFileCache()`，否则宿主 90 秒内可能返回旧数据。

### 3.3 事件总线

~~~csharp
ctx.Events.ServerStateChanged += up   => ctx.Log("服务端 " + (up ? "上线" : "下线"));
ctx.Events.WorldChanged       += w    => ctx.Log("切到世界 " + w);
ctx.Events.WorldListRefreshed += list => ctx.Log("世界数 " + list.Count);
ctx.Events.MetricsSampled     += s    => { /* s.CpuPercent / s.MemoryGb / s.UpKbPerSec / s.DownKbPerSec / s.At */ };
ctx.Events.TerminalLine       += l    => { /* l.Text / l.Level —— 高频！ */ };
ctx.Events.ShuttingDown       += ()   => { /* 保存状态 */ };
~~~

| 事件 | 触发时机 | 备注 |
|:--|:--|:--|
| `ServerStateChanged(bool)` | 服务端上线/下线（REST 心跳探测） | 与 `ServerOnline` 同源 |
| `WorldChanged(string)` | 左侧世界列表选中项变化 | |
| `WorldListRefreshed(list)` | 世界列表内容变化 | |
| `MetricsSampled(MetricsSample)` | 约 2 秒一次 | 处理器要快，别阻塞 |
| `TerminalLine(TerminalLineEvent)` | 终端新增一行 | ☠ 高频（服务端日志每秒几十行）；**没订阅时零开销** |
| `ShuttingDown()` | 管理器关闭 | 之后会调用 `OnUnload` |

### 3.4 数据模型

| 类型 | 形状 |
|:--|:--|
| `WorldInfo` | `Name` / `Size` / `Running` |
| `PluginFileInfo` | `Name` / `IsDirectory` / `Size` / `Modified` / `SizeText`（如 `521 B` / `1.4 KB`） |
| `MetricsSample` | `CpuPercent` / `MemoryGb` / `UpKbPerSec` / `DownKbPerSec` / `At` |
| `TerminalLineEvent` | `Text` / `Level` |
| `PluginCommandArgs` | `Name` / `Args` / `RawArguments` / `TargetWorld`（全部世界时为 null）/ `Context` / `Reply(text)` |
| `FileActionContext` | `RelativePath`（用 `/` 分隔）/ `FullPath` / `Directory` / `IsDirectory` / `Size` |
| `MapViewState` | `Title` / `IsWorld` / `IsStitched` / `IsTemplate` / `TilesWide` / `TilesHigh` / `WorldRelPath` |
| `MapSelection` | `X` / `Y` / `Width` / `Height`（图格坐标） |

### 3.5 可选扩展接口（上下文挂载）

除了 `IManagerPlugin`，**再实现**下面任一接口，就能把功能挂到**用户手边的位置**（而不是只能另开一整页）：

| 接口 | 挂到哪 | 能拿到什么 |
|:--|:--|:--|
| `IMapContributionPlugin.ContributeMap(IMapContributionHost)` | 内置「地图预览」页工具栏 | 加按钮 / 加任意控件 / 开关框选模式 / 读当前视图与选区 / 显示 `.utpl` 模板 / 写状态行 |
| `IFileActionPlugin.ContributeFileActions(IFileActionHost)` | 内置「文件管理」页**右键菜单** | `RegisterFileAction(文本, onClick, visible?)`；`visible=null` 表示总显示，否则每次弹菜单按当前条目求值 |

~~~csharp
public sealed class MyPlugin : IManagerPlugin, IFileActionPlugin
{
    private IManagerContext? _ctx;
    public void OnLoad(IManagerContext ctx) { _ctx = ctx; }

    public void ContributeFileActions(IFileActionHost host)
    {
        host.RegisterFileAction("🔧 用我的插件处理这个文件", async info =>
        {
            _ctx?.Log($"选了 {info.RelativePath}（{info.Size} 字节）");
            await Task.CompletedTask;
        },
        visible: info => !info.IsDirectory && info.RelativePath.EndsWith(".json"));
    }
}
~~~

> 可运行示范：`extensions\ApiProbe`（注册两个动作，一个总显示、一个只在 `.json` 上出现）。
> 插件重载时宿主会**自动清空**这些注册（避免委托把插件的可回收 ALC 钉住），无需手动反注册。

---

### 3.6 多服务器（多档）：插件要改什么、不用改什么

管理器现在能同时管多台服务器（`<管理器目录>\servers.json`，每台一条"档"；点左侧服务器卡片 = **切档**）。对插件只有一句结论：

> **插件永远只看到"当前激活档"。** 没有"给第 2 档单独传参数"的 API，也不需要 —— 所有连接类成员都是惰性回调，切档后自动指向新档。

**插件看到的 = 激活档**

| 成员 | 随激活档变 | 实现 |
|:--|:--|:--|
| `DeployRoot` / `RestBase` / `Token` / `FilesRoot` | ✅ 变 | `InitPluginHost` 传的是 lambda，**取值那一刻**才读激活档 |
| `Worlds` | ✅ 变 | 同上（激活档的世界列表快照） |
| `ServerOnline` | ✅ 变 | 管理器心跳探测结果，切档后按新档重探 |
| `DataDir` / `GetConfig` / `SaveConfig` | ✅ 变（**换插件实例后才生效**，见下） | `plugin-data` 根目录跟着档走 |
| 命令 / 指标 / 状态 | ❌ **全局一套** | 只有一份插件宿主；回调被调用时读到的是"那一刻的激活档" |

**数据目录按档隔离**（由 `servers.json` 每条档的 `legacyDataRoot` 决定）：

| 档的来源 | `ctx.DataDir` 实际路径 |
|:--|:--|
| 从旧 `connection.json` 迁移出来的第一档（`"legacyDataRoot": true`） | `<管理器目录>\plugin-data\<插件名>\` —— **沿用旧的全局目录**，老配置不搬家 |
| 之后新建 / 复制的每一档 | `<管理器目录>\plugin-data\<档id>\<插件名>\`（档 id 会消毒，最长 40 字符） |

发布目录实测：`plugin-data\` 下同时存在 `定时任务\config.json`（迁移档的全局目录）与 `srv-local-e472eb\定时任务\`、`srv-1a17af55\定时任务\`（新档目录）。

**切档时到底发生什么（默认 + 开关）**

`servers.json` 顶层有两个新键。**没有 UI，改完要重启管理器**才生效（启动时只读一次）；文件里不写 = 用默认值：

~~~json
{ "reloadPluginsOnSwitch": true, "bgLogSubs": 4 }
~~~

| 行为 | 默认 | 开关 | 影响 |
|:--|:--|:--|:--|
| 切档后**重载插件** | 开 | `reloadPluginsOnSwitch` | 去抖 **1.5 秒** + 等"切档空档"（最多等 **10 秒**）；已经按同一档加载过就**不重复重载**；重载期间 `[插件] 已加载` / `[API 探针]` 这类逐条日志被折叠成一行：`插件已按「<档名>」重载：N 个，用时 xxx ms（逐条加载日志已折叠）` |
| 插件**内存态**是否跨档 | 不跨档（靠重载） | `reloadPluginsOnSwitch` | 关掉后 Lua 定时任务的"上次执行时间"、插件自己的计数器**跨档共用** |
| `ctx.DataDir` 换目录 | 是，跟重载一起换 | 同上 | 关掉后宿主会把 `DataRoot` 指向新档**并让所有插件的 `DataDir` 缓存失效**（`PluginHost.InvalidateDataDirs()`，2026-10-17 修）→ 下次访问 `DataDir`/`GetConfig` 就是新档目录，不必重载。⚠ 但插件**自己缓存**过的路径/字段不会自动更新，仍要"用时现取" |
| `Events.ServerStateChanged` | 切档后重新探测 | — | 切档时 `lastServerUp` 被清空 → 新档只要探到在线，就会**再发一次 `true`**（哪怕切档前也是在线）；新档本来就离线则**不发** `false`（未知不发事件），等它上线才发。所以别指望"切档 = 先收到一次 `false`" |
| 非激活档的**日志** | 不进插件事件 | `bgLogSubs` | `Events.TerminalLine` **只收激活档**的日志；后台档的 `/stream/log` 由管理器直接灌进该档终端缓冲，不经过事件总线 |
| 后台日志**连接数** | 4 | `bgLogSubs`（`0` = 不订阅） | 每条后台流一条长连接；**实际生效 = min(bgLogSubs, 4)**（管理器硬上限 `MaxBackgroundLogSubs = 4`，写更大也只取 4）；只有"远端 + 有 ssh"的非激活档才订阅（本机档不订阅） |

**三条给插件作者的可执行建议**

**a) 要"按档保存状态"，就用 `ctx.DataDir` / `GetConfig`。** 它已经随档变（新档 = `plugin-data\<档id>\<插件名>\`）；**别**把状态写死在 dll 旁边（`ctx.PluginDir`）或 `<管理器目录>` 根下 —— 那样两台服务器会互相覆盖：

~~~csharp
public void OnLoad(IManagerContext ctx)
{
    ctx.Log($"[我的插件] 本档数据目录 = {ctx.DataDir}");
    var cfg = ctx.GetConfig<MyConfig>();          // <DataDir>\config.json（需 config 权限）

    // ❌ 别这么干：路径不随档变，所有服务器共用一份
    // File.WriteAllText(Path.Combine(ctx.PluginDir, "state.json"), json);
}
~~~

**b) 别在 `OnLoad` 里把 `Worlds` / `DeployRoot` / `RestBase` / `Token` / `DataDir` 缓存进静态字段**（切档会变，`DataDir` 还会在重载时变）；**要用时现取** —— 背后是 lambda，代价极低：

~~~csharp
// ❌ static string _root = ctx.DeployRoot;     // 切档后还是第一台的值
// ✅ 每次用的时候现取
string root = _ctx!.FilesRoot;
foreach (var w in _ctx.Worlds) { /* … */ }
~~~

自己算出来的派生值（拼好的绝对路径、缓存的令牌串）同理：要么现算，要么在 `ServerStateChanged` / 下次使用时重算。

**c) 关掉 `reloadPluginsOnSwitch` 的取舍**

| 做法 | 得到 | 失去 |
|:--|:--|:--|
| 保持默认（重载） | 插件内存态严格按档隔离，`DataDir` / `Worlds` / `Token` 全部干净切换 | 每次切档付一次重载开销：**约 4~26 秒**（源码注释里的实测值；单次 Reload 2.3~12.8 秒，大部分花在各插件自己的 `OnLoad` —— ApiProbe 一轮自检含 ssh、Lua 编译） |
| `"reloadPluginsOnSwitch": false` | 省掉这笔开销，切档更快 | 插件内存态（Lua 定时任务上次执行时间、计数器、**插件自己缓存的字段**）**跨档共用**；`DataDir` 会跟着换（宿主会失效缓存），但你在 `OnLoad` 里存进字段的旧路径不会变 |

**什么时候该关**：插件全部是"无状态、每次用现取"的（纯 REST 面板、只读展示），且切档频繁、嫌重载慢。
**什么时候必须开**：插件有定时任务 / 计数器 / 按档配置（`LuaTaskScheduler` 是典型），或者你在 `OnLoad` 里缓存了任何与连接相关的值。

**自测多档（5 步）**

1. 左侧「＋」建第二档（名字随便，端口/ssh 按需）→ 保存；`servers.json` 的 `servers[]` 会多一条，`"legacyDataRoot": false`。不想点界面：`UTSLManager.exe --server "<档id 或档名>"` 可启动即激活指定档。
2. 插件里把关键值露出来：`ctx.RegisterStatus("我的插件 · 档", () => ctx.DataDir)`，或 `ctx.Log($"[我的插件] DataDir={ctx.DataDir} RestBase={ctx.RestBase} 世界数={ctx.Worlds.Count}")`。
3. 点第二档卡片切过去 → 等约 2~12 秒 → 终端出现 `插件已按「<档名>」重载：…（逐条加载日志已折叠）`。
4. 核对三件事：`DataDir` 变成 `plugin-data\<新档id>\<插件名>`；`RestBase` 是新档的端口；`Worlds` 是新档的世界。
5. 切回第一档：`DataDir` 回到 `plugin-data\<插件名>`（迁移档）—— 两档各自的 `config.json` 内容互不覆盖 = 隔离成立。想验证"关掉重载"：把 `"reloadPluginsOnSwitch": false` 写进 `servers.json` → **重启管理器** → 再切档，终端只出现一行 `已切档但不重载插件（reloadPluginsOnSwitch=false）：DataRoot→…`，插件不再重载。

---

## 4. 清单：manifest.json

文件名优先 `<插件名>.manifest.json`（Lua 用 `plugin.manifest.json`），找不到再找 `manifest.json`。**缺失或 JSON 损坏都不影响加载**（按“无清单”处理）。

~~~json
{
  "name": "我的插件",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "一句话说明",
  "entry": "MyPlugin.MyPlugin",
  "type": "view",
  "apiVersion": 6,
  "permissions": ["rest", "config", "fs"],
  "dependencies": ["OtherPlugin"]
}
~~~

| 字段 | 说明 |
|:--|:--|
| `name` | 插件名；用于依赖匹配与 UI |
| `version` | 版本；进加载日志（缺省显示 `v?`） |
| `author` / `description` | 作者 / 说明（插件 `Author` 为空时回退） |
| `entry` | 入口类型（全名或短名）；留空 = 扫描全部实现 |
| `type` | `view` / `action` / `service`，仅标注用途 |
| `apiVersion` | 要求的宿主 API 版本（缺省 = 1）；高于宿主会**拒绝加载并给出提示** |
| `permissions` | 见 §5；**不写 = 全部允许**，写 `[]` = 全部禁止 |
| `dependencies` | 依赖的其他插件（按 name 或 dll 名匹配）；缺失/失败会跳过依赖者 |

---

## 5. 权限模型

| 权限 | 管什么 |
|:--|:--|
| `rest` | `RestGet` / `RestPost` |
| `fs` | `ReadDeployText` / `WriteDeployText` 与全部文件 API |
| `config` | `GetConfig` / `SaveConfig` |
| `exec` | `RunCommand`（部署主机执行 PowerShell）**以及启停服务端** —— 能力很强，谨慎使用 |
| `net` | `HttpGet` / `HttpPost` |

- **不写 `permissions`** → 全部允许（向后兼容，老插件照常跑）；**写 `[]`** → 全部禁止，调用受限 API 抛 `PluginPermissionException`。
- `Log` / 事件 / 命令 / 指标 / `DataDir` / `ServerOnline` **不需要**权限。

> ⚠️ **诚实说明**：这是“声明 + 对管理器所提供 API 的强制”，**不是安全沙箱**。插件同进程运行，仍可直接用 `System.IO` / `HttpClient`。
> 它的价值是「让作者声明意图 + 让用户知道边界 + 防误用」，不要当成防恶意插件的机制；**来源不可信的插件不要装**。

---

## 6. 目录布局与加载顺序

管理器扫描 **`<UTSLManager.exe 所在目录>\plugins\`**：

~~~
plugins\
├─ MyPlugin.dll                  ← 扁平：插件本体
├─ MyPlugin.manifest.json        ← 清单（扁平时用同名清单）
├─ MyScript.csx                  ← C# 脚本插件
├─ Disabled.dll.disabled         ← 停用 = 改后缀（加载器只扫 .dll / .csx / .lua）
├─ LuaTaskScheduler\
│  ├─ plugin.lua                 ← Lua 插件（一插件一目录）
│  └─ plugin.manifest.json
└─ WithDeps\                    ← 带私有依赖时推荐这种布局
   ├─ WithDeps.dll
   ├─ WithDeps.manifest.json
   └─ 你的私有依赖.dll
~~~

每个插件有独立数据目录（即 `ctx.DataDir`），**按服务器档隔离**（多档细节见 §3.6）：

| 档 | `ctx.DataDir` |
|:--|:--|
| 从旧 `connection.json` 迁移来的第一档 | `<管理器目录>\plugin-data\<插件名>\`（沿用旧的全局目录，老配置不搬家） |
| 之后新建的每一档 | `<管理器目录>\plugin-data\<档id>\<插件名>\` |

**加载流程与错误隔离**：

~~~
管理器启动（或点「重新加载」）
  └─ PluginHost.Load()
       ├─ 卸载旧的一代（OnUnload → 清空命令/指标/订阅 → ALC.Unload）
       └─ PluginLoader.Load()
            ├─ 扫描 plugins\（扁平 + 一插件一目录；*.dll / *.csx / *.lua）
            ├─ 读清单 → 校验 apiVersion → 依赖拓扑排序（循环依赖会被检出）
            ├─ dll → 可回收 ALC 内存加载；csx → Roslyn 编译；lua → Lua 适配器
            ├─ 找 IManagerPlugin 实现（或 manifest.entry 指定）
            ├─ 创建实例 → OnLoad(ctx)
            └─ 成功项进「扩展」菜单；失败项进 PluginLoadReport
~~~

**三重隔离**：① 单个类初始化失败 → 跳过该类；② 单个文件加载失败（坏 IL / 缺依赖 / API 不符）→ 跳过该文件，管理器照常启动；③ `CreateView` 抛异常 → 插件页显示红字，管理器不崩。事件处理器、命令处理器、定时器也都有兜底（异常写终端）。

**「🧩 插件管理」页**（**找不到插件先看这里**，不要只看终端）：已加载列表（图标/名称/版本/API/类型/作者/来源/权限/命令 + 停用按钮）、**加载失败列表**（含具体原因）、已停用文件的一键启停、顶部（插件目录、计数、宿主 API 版本、重新加载、打开目录、目录监视、插件语言）。

---

## 7. 写界面（Avalonia）

- 推荐**纯 C# 建控件**（示例都是这样），插件工程最简单；也可自带 `.axaml`，但要自己配 `AvaloniaResource`（管理器不会帮你编译 XAML）。
- 界面**会被缓存**（切走再切回不重建），可以把控件引用存字段里就地更新。
- **不要在 UI 线程做阻塞操作**（同步 `HttpClient`、`Thread.Sleep`、大文件 IO）→ 用 `await` 或 `RunBackground`。
- 配色对齐管理器深色主题：

| 用途 | 颜色 |
|:--|:--|
| 主文字 / 次要 / 弱化 | `#F2F2F5` / `#B9B9C0` / `#7C7C82`（`#6E7681`） |
| 强调 / 错误 | `#66CCFF` / `#FF7B72` |
| 卡片底 / 描边 | `#141417` / `#2A2A30` |

---

## 8. 脚本插件（.csx / .lua）

### 8.1 .csx 脚本

不想建工程？直接写脚本。Roslyn 编译后**走与 dll 插件完全相同的流程**（清单 / API 校验 / 依赖 / 可回收 ALC）。

~~~csharp
// plugins\hello.csx   （同目录再放 hello.manifest.json）
using UTSLManager.Plugins;
using Avalonia.Controls;

public sealed class ScriptHello : IManagerPlugin
{
    public string Name => "脚本示例";
    public string Icon => "📜";
    public string Description => "用 .csx 写成的管理器插件";
    public string Author => "script";
    public int RequiredApiVersion => ManagerApi.Version;

    public void OnLoad(IManagerContext ctx) => ctx.Log("[脚本示例] 已加载");
    public Control CreateView(IManagerContext ctx) => new TextBlock { Text = "来自脚本的界面" };
}
~~~

~~~json
{ "name": "脚本示例", "version": "1.0.0", "entry": "ScriptHello", "apiVersion": 6 }
~~~

注意：`entry` 可写**短名**（脚本里的类型真名是 `Submission#0+ScriptHello`，短名匹配即可）；编译失败时「插件管理」页会显示诊断（`CS####: 消息`）；可用引用 = 契约程序集 + Avalonia 系列 + BCL，并自动 import 了 `UTSLManager.Plugins` / `Avalonia*` / `System*`。

### 8.2 Lua 插件

**社区里有 Lua 作者，所以专门支持。** 放 `plugins\xxx.lua`（扁平）或 `plugins\<名>\plugin.lua`（推荐，可带 `plugin.manifest.json`）。顶层直接写代码即可，宿主把 `ctx` 注入为全局变量。

~~~lua
ctx:meta{ name = "我的 Lua 插件", icon = "🎐", description = "一句话", author = "你" }

ctx:log("加载完成")
ctx:every(60, function() ctx:log("每分钟一次：" .. ctx:now().str) end)   -- 计时器（回调在后台线程）
~~~

**ctx 能力一览**（与 C# 的 `IManagerContext` 一一对应）：

| Lua | 说明 |
|:--|:--|
| `ctx:log(s)` | 写进管理器终端 |
| `ctx:now()` | `{year,month,day,hour,min,sec,wday,epoch,str}`（`wday` 1=周一…7=周日；所以 `os` 可以整个禁掉） |
| `ctx:every(sec, fn)` / `ctx:after(sec, fn)` / `ctx:cancel(id)` | 周期 / 一次性 / 取消（回调都在**后台线程**） |
| `ctx:get_config()` / `ctx:save_config(t)` | Lua 表 ↔ `<DataDir>\config.json`（中文按原样写，方便手改） |
| `ctx:register_command(name, desc, fn)` | 终端 `!name 参数` 触发；`fn(raw, world)` |
| `ctx:register_metric(name, unit, fn)` / `ctx:register_status(name, fn)` | 右侧「插件指标」区（宿主**从后台线程**调用，别做慢操作） |
| `ctx:rest_get(url)` / `ctx:rest_post(url)` | 当前连接的 UTSL REST（自动带令牌，需 `rest`） |
| `ctx:run_command(ps, timeoutMs)` | 部署主机执行 PowerShell（需 `exec`） |
| `ctx:read_text` / `write_text` / `exists` / `list_files` | 部署目录文件（需 `fs`；远端/本机统一） |
| `ctx:worlds()` | 世界列表快照 |
| `ctx:server_online()` | 服务端是否在线（心跳探测结果） |
| `ctx:server_start(reason)` / `ctx:server_stop(reason)` | 启停服务端进程（需 `exec`；与管理器按钮同一条路径） |
| `ctx:L(key, fallback)` | i18n |
| `ctx:register_panel(title, fn)` + `ctx:on_action(fn)` | 声明式界面（见下） |
| 只读字段 | `ctx.plugin_name` / `plugin_dir` / `data_dir` / `files_root` / `language` |

**界面是“声明式”的**：Lua 不能返回 Avalonia 控件（会踩类型身份分裂），而是返回一份控件描述，宿主渲染**真控件**，按“**标签左、控件右**”两列布局：

| kind | 说明 |
|:--|:--|
| `text` | 只读文本（`label` + `value`） |
| `text_input` | 单行输入（`id` + `value`） |
| `number` | 数字输入 |
| `checkbox` | 真复选框（布尔值） |
| `select` | 下拉（`options` 数组 + `value`） |
| `time` | ★ **钟表盘**：外环分钟=橙、内环小时=蓝，**拖动指针**或点盘面即可；右侧带「现在」按钮。值 `HH:mm` |
| `time_simple` | 传统两个下拉的时间选择器 |
| `date` | 日历选日期。值 `yyyy-MM-dd` |
| `list` | 可点选列表（`items = { {text, sub, active, key} }`）；点击回调 `action=select`，被点项的 `key` 在 `values.clicked` 里 |
| `buttons` | 按钮组（`items = { {text, action} }`） |
| `section` / `hint` / `divider` | 分节标题 / 灰色说明行 / 分隔线 |

~~~lua
ctx:register_panel("我的面板", function()
  return {
    { kind = "section", label = "基本设置" },
    { kind = "text",     label = "状态",  value = "运行中" },
    { kind = "checkbox", id = "enabled", label = "启用", value = true },
    { kind = "select",   id = "mode",  label = "模式", options = { "每天", "每周" }, value = "每天" },
    { kind = "number",   id = "every", label = "间隔（秒）", value = 60 },
    { kind = "time",     id = "at",    label = "执行时刻", value = "04:00" },
    { kind = "date",     id = "date",  label = "指定日期", value = "2026-10-20" },
    { kind = "list",     items = { { text = "A 任务", sub = "每天 04:00", active = true, key = "1" } } },
    { kind = "buttons",  items = { { text = "保存", action = "save" } } },
  }
end)
ctx:on_action(function(action, values)   -- values.<id> 是各控件当前值；列表点击时 action=select
  ctx:log("点了 " .. action .. "，间隔=" .. tostring(values.every))
end)
~~~

**安全边界（实测，探针 `probes\LuaSpike`）**：MoonSharp 官方**软沙箱** + 宿主**额外剔除** `os`、`package`（实测软沙箱仍保留这两个）；`io / load / loadstring / dofile / require / debug` 均不可用；宿主**不注册任何 .NET 类型**给 Lua，唯一通道就是 `ctx` 上的这些函数。

★ **诚实的边界**：MoonSharp 没有内建指令计数 → 写错的死循环会**占满一个 CPU 核**；但所有 Lua 回调都在**后台线程**执行（实测：Lua 死循环期间管理器计时器照常跳动），所以**界面不会被冻住**。这与“C# 插件本来就能 `RunCommand`”是同一级信任模型。

---

## 9. 完整示例索引

**都在仓库 `extensions\` 下，直接打开抄即可。**

| 示例 | 形式 | 演示什么 |
|:--|:--|:--|
| `LuaTaskScheduler` | Lua | ★ **定时任务**：间隔（秒）/ 每天 / 每周几 / 每月几号 / **指定日期（日历选）**；动作 = REST（默认）/ PowerShell / **启停服务端**；**仅在线时执行 + 10 分钟宽限补跑**；面板 = **可点选任务列表** + 两列表单 + 执行记录 |
| `ApiProbe` | C# | **API 自检**：逐项调用各能力并把结果写进自己的数据目录；同时演示 **文件页右键动作**（两个：一个总显示、一个只在 `.json` 上出现） |
| `PacketInspector` | C# | 最完整的 C# 参考：REST 轮询、命令、指标、状态、工具栏、按需解码与限流 |
| `MapTemplates` | C# | **地图扩展点**：在「地图预览」页加按钮、框选、导出/浏览 `.utpl` 模板（它还引用了管理器本体以使用 `UTSLManager.Maps.*`） |
| `RuYouMusicPanel` | C# | 纯 REST 面板型插件（点歌队列 / 歌词滚动） |

**自动化验收探针**（想确认自己没改坏时用；都在 `utsl-verify\probes\`，可复跑）：

| 探针 | 验证什么 |
|:--|:--|
| `LuaSchedCheck` | 加载**真实发布的那份 `plugin.lua`**，用桩 ctx 跑 **28 个用例**（秒级间隔边界 / 宽限补跑 / 每周 / 每月 / 指定日期 / 描述文案 / **动作分派是否真的调到 rest·cmd·start·stop**）→ 当前 **28/28** |
| `ContractCheck` | 契约拆分后**旧插件 dll 无需重编**仍能加载、类型身份统一 |
| `ClockMath` | 钟表时间选择器的角度数学（20 项 + 84 个往返用例）**并离屏渲染成 PNG** |
| `LuaSpike` | Lua 沙箱边界（哪些库不可用）+ 死循环不冻界面 |
| `LintCheck` / `ColorCheck` | 管理器自身编辑器：语法校验逻辑 / 配色调优前后值 |

---

## 10. 硬性规则（踩过的坑）

1. **不要把 `UTSLManager.dll` / `UTSLManager.Plugin.Abstractions.dll` / 任何 `Avalonia*.dll` 复制进 `plugins\`。**
   引用必须 `<Private>false</Private>`（并 `CopyLocalLockFileAssemblies=false`）。第二份同名程序集会破坏类型统一 → 管理器**静默不加载**你的插件。
2. **Avalonia 版本必须与管理器一致**（当前 `12.1.2`）；浮动 `12.*` 可能拉到不兼容版。
3. **`TargetFramework` 必须与管理器一致**（当前 `net10.0`）。
4. 停用插件**不要删文件**，改名 `X.dll.disabled` / `X.csx.disabled`（加载器只扫 `.dll` / `.csx` / `.lua`）。
5. `OnLoad` 时服务端可能还没起来 → 别在 `OnLoad` 里同步依赖 REST / 世界列表（判 `ServerOnline` 或等事件）。
6. `TerminalLine` 是高频事件（服务端日志），处理器要极快，别在里面做 IO。
7. 用了第三方 NuGet 依赖请用**一插件一目录**布局，把依赖 dll 一起放进去（ALC 会按 `.deps.json` / 目录解析）。
8. 插件**不要在静态字段里长期持有界面控件或管理器对象**，否则热重载时 ALC 收不回来（管理器已清空菜单/视图缓存/订阅，剩下的是插件的责任）。

---

## 11. 排错 FAQ

**Q：右侧「扩展」里没有我的插件？**
→ 先看「🧩 插件管理」页的**失败列表**（原因都写在那里）；再确认文件在 `<UTSLManager.exe 目录>\plugins\`（**不是** `utsl-deploy\plugins`）；确认类是 `public`、非抽象、有无参构造；确认 `plugins\` 里没有混入 `UTSLManager.dll` / `Avalonia*.dll`（最常见的静默失败）；最后点「重新加载」。

**Q：提示“需要 API vN，本管理器支持到 vM”？**
→ 插件要求的版本高于宿主：更新管理器，或把 `apiVersion` / `RequiredApiVersion` 降下来。

**Q：`TypeLoadException` / `FileNotFoundException: Avalonia.Controls`？**
→ `plugins\` 里混进了旧版/重复的 Avalonia，删掉只留插件自己的文件。

**Q：`.csx` 或 `.lua` 加载失败？**
→ `.csx` 看「插件管理」页的编译诊断（常见：缺 `using`、类型名与 `entry` 对不上）；`.lua` 看终端里的 `[Lua] …` 报错（宿主会把 Lua 异常写进终端）。

**Q：插件命令没反应？**
→ 命令要用 `!名字` 触发（`/` 开头是发给服务端的 TShock 指令）；`!help` 列出所有插件命令；命令异步执行，结果稍后打在终端。

**Q：改了代码不生效？**
→ 没 build / 没复制 / 没重载。管理器运行时文件不再被锁，可直接覆盖；点「重新加载」，或打开「监视目录自动重载」（0.9 秒防抖）。

**Q：REST 调用失败？**
→ 服务端没起、端口/令牌不对（「连接设置」），或没装 `WorldCommandChannel`；也要确认 manifest 里声明了 `rest` 权限。

**Q：往「文件管理」页注册的右键动作没出现？**
→ 确认类**同时**实现了 `IFileActionPlugin`；确认 `visible` 判断没有把条目过滤掉；注册发生在加载完成后，热重载会重新注册。

---

## 12. 实现位置（想读源码时）

| 文件 | 作用 |
|:--|:--|
| `UTSLManager.Plugin.Abstractions\IManagerPlugin.cs` | `IManagerPlugin` + `IManagerContext` 契约（**独立程序集，插件只引用它**） |
| `UTSLManager.Plugin.Abstractions\Contracts.cs` | 版本常量、`WorldInfo` / `PluginFileInfo` / `MetricsSample` / `TerminalLineEvent` / `PluginCommandArgs` / 指标与状态模型 |
| `UTSLManager.Plugin.Abstractions\ManagerEvents.cs` | 事件总线（每次重载换新实例） |
| `UTSLManager.Plugin.Abstractions\IMapContribution.cs` / `IFileAction.cs` | 两个可选扩展接口（地图页 / 文件页） |
| `Plugins\PluginModels.cs` | **宿主内部**模型：描述符、加载报告、清单解析（插件看不到） |
| `Plugins\TypeForwards.cs` | 契约类型转发（保证旧插件 dll 无需重编） |
| `Plugins\PluginLoadContext.cs` | 可回收 ALC（共享宿主程序集、内存加载、私有依赖解析） |
| `Plugins\PluginLoader.cs` | 扫描 / 清单 / 依赖排序 / dll·csx·lua 加载 / 错误隔离 |
| `Plugins\PluginHost.cs` | 宿主：生命周期、命令、指标、状态、权限、配置、i18n、服务端控制注入点 |
| `Plugins\LuaPluginHost.cs` | Lua 运行时：适配器、ctx 桥、计时器、声明式面板 |
| `Controls\ClockTimePicker.cs` | 钟表式时间选择器（外环分钟 / 内环小时，可拖动） |
| `MainWindow.axaml(.cs)` | 「扩展」菜单、插件视图、插件管理页、事件触发、`!命令` 派发、文件页右键菜单挂载；`InitPluginHost` / `ScheduleProfilePluginReload`（切档重载插件） |
| `Servers\ServersConfig.cs` | `servers.json` 多档：`PluginDataRoot()`（插件数据按档隔离）、`LegacyDataRoot`、`ReloadPluginsOnSwitch`、`BgLogSubs` |

