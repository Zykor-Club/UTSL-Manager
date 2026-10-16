# UTSLManager 插件开发指南（API v3）

> 想让管理器长出新功能，又不想改管理器源码？写一个管理器插件。
> 插件是独立工程（或一个 `.csx` 脚本），丢进 `plugins\` 即可，管理器启动时自动加载。

**最后更新：2026-10-11　适用：UTSLManager (Avalonia) / .NET 10 / Avalonia 12.1.2 / 插件 API v3**

> **v3 新增（本次）**：统一**文件 API**（`FilesRoot`/`ListFiles`/`ReadText`/`WriteText`/`CreateDirectory`/`Rename`/`Delete`/`Upload`/`Download`/`InvalidateFileCache`）——
> **远端模式自动走 SSH/scp，本机模式直接读盘**，插件不再需要关心目标在哪；另加 `RunCommand`（`exec` 权限）与 `HttpGet`/`HttpPost`（`net` 权限）。
> v2 插件无需改动（`RequiredApiVersion` 默认 v1，老 API 全部保留）。

---

## 0. 先分清两种“插件”

| | **管理器插件**（本文档） | 服务端插件 |
|:--|:--|:--|
| 跑在哪 | UTSLManager 进程里 | UTSL 服务端进程里 |
| 放哪 | `AvaloniaUTSL\plugins\` | `utsl-deploy\plugins\` |
| 用什么 API | `IManagerPlugin` / `IManagerContext`（Avalonia UI） | TShock / UTSL API |
| 能干什么 | 加界面、订阅管理器事件、加终端命令/指标、调度 REST、读写部署目录 | 改服务端玩法、发指令、包处理 |

两者互不相干：右侧「📁 文件管理」管的是**服务端插件**；管理器插件在「🧩 插件管理」页和右侧「扩展」分组。

---

## 1. 30 秒快速开始

### 1.1 目录

~~~
MyPlugin/
├─ MyPlugin.csproj          # 独立工程：引用管理器的 UTSLManager.dll
├─ MyPlugin.cs              # 实现 IManagerPlugin
├─ manifest.json            # 可选：版本 / API / 权限 / 依赖 / 入口
└─ README.md
~~~

### 1.2 csproj（照抄，重点看注释）

~~~xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>        <!-- 必须与管理器一致 -->
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>MyPlugin</AssemblyName>

    <!-- ★ 只输出插件自己的 dll：否则 Avalonia 几十个 dll 会灌进 plugins\ -->
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <!-- 管理器目录（含 UTSLManager.dll）；用 -p:ManagerDir=... 覆盖 -->
  <PropertyGroup>
    <ManagerDir Condition="'$(ManagerDir)' == ''">C:\Users\星梦\Desktop\AvaloniaUTSL</ManagerDir>
    <PluginDeployDir Condition="'$(PluginDeployDir)' == ''">$(ManagerDir)\plugins</PluginDeployDir>
  </PropertyGroup>

  <ItemGroup>
    <!-- 编译期需要 Avalonia 界面类型；版本必须与宿主一致 -->
    <PackageReference Include="Avalonia" Version="12.1.2" />

    <!-- ★ P0 起：插件只需引用【契约程序集】（约 26KB）。命名空间仍是 UTSLManager.Plugins，using 不用改。
         Private=false 必须要有：运行时用管理器已加载的那一份；
         复制出第二份会让类型标识不一致，管理器认不出你的插件（且不一定报错）。 -->
    <Reference Include="UTSLManager.Plugin.Abstractions">
      <HintPath>$(ManagerDir)\UTSLManager.Plugin.Abstractions.dll</HintPath>
      <Private>false</Private>
    </Reference>

    <!-- 只有需要调用宿主的地图/渲染等公开 API（UTSLManager.Maps.*）时才再引管理器本体；
         纯动作 / 服务 / 面板插件不需要它。参考 extensions\MapTemplates（两个都引）。
         ★ 顺带说明：管理器里保留了 TypeForwardedTo，所以【按旧版 UTSLManager.dll 编译的插件 dll 无需重编】
           也能继续加载 —— 老插件不会因为这次拆分而失效。 -->
    <!--
    <Reference Include="UTSLManager">
      <HintPath>$(ManagerDir)\UTSLManager.dll</HintPath>
      <Private>false</Private>
    </Reference>
    -->
  </ItemGroup>

  <!-- 可选：构建后自动部署（管理器在跑时文件被占用 → 只警告、不让 build 失败）-->
  <Target Name="DeployToManager" AfterTargets="Build">
    <MakeDir Directories="$(PluginDeployDir)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(PluginDeployDir)"
          ContinueOnError="true" Retries="0" />
  </Target>
</Project>
~~~

### 1.3 最小插件（API v2）

~~~csharp
using System;
using Avalonia.Controls;
using UTSLManager.Plugins;

public sealed class MyPlugin : IManagerPlugin
{
    public string Name        => "我的插件";
    public string Icon        => "✨";
    public string Description => "一句话说明";
    public string Author      => "你的名字";

    // 用 v2 能力（事件/命令/指标/配置/i18n）就声明 v2；只用 v1 可省略
    public int RequiredApiVersion => ManagerApi.Version;

    public void OnLoad(IManagerContext ctx)
    {
        ctx.Log("[我的插件] 已加载");
        ctx.RegisterCommand("hi", "打个招呼", args => { args.Reply("hi " + args.RawArguments); return System.Threading.Tasks.Task.CompletedTask; });
        ctx.RegisterMetric("我的插件 · 调用", "次", () => 0);
        ctx.Events.ServerStateChanged += up => ctx.Log("[我的插件] 服务端 " + (up ? "上线" : "下线"));
    }

    public void OnUnload() { /* 保存状态 / 停后台任务 */ }

    public Control? CreateView(IManagerContext ctx) => new TextBlock { Text = "你好，管理器！", FontSize = 16 };
}
~~~

### 1.4 构建 · 部署 · 验证

~~~powershell
dotnet build -c Release
# 管理器不在默认目录时：
dotnet build -c Release -p:ManagerDir="D:\path\to\AvaloniaUTSL"
~~~

部署 = 把 `MyPlugin.dll`（+ 可选 `MyPlugin.manifest.json`）放进 `<管理器目录>\plugins\`，然后重启管理器（或打开「🧩 插件管理 → 重新加载」）。

成功的信号：
1. 右侧「扩展」分组出现你的插件按钮；
2. 「🧩 插件管理」页显示插件名 / 版本 / API / 权限；
3. 管理器终端打印 `[插件] 已加载 ✨ 我的插件 v1.0.0（MyPlugin.dll，作者 …）`。

---

## 2. 目录与文件约定

管理器扫描 **`<UTSLManager.exe 所在目录>\plugins\`**，支持三种布局：

~~~
plugins/
├─ MyPlugin.dll                  ← 扁平：插件本体
├─ MyPlugin.manifest.json        ← 可选清单（扁平时请用同名清单）
├─ MyScript.csx                  ← 脚本插件（P2-12）
├─ MyScript.manifest.json
├─ Disabled.dll.disabled         ← 停用=改后缀，加载器只扫 .dll / .csx
└─ WithDeps/                     ← 一插件一目录（带私有依赖时推荐）
   ├─ WithDeps.dll
   ├─ WithDeps.manifest.json
   └─ 你的私有依赖.dll
~~~

- 一个 dll / csx 里可以有多个 `IManagerPlugin` 实现（会被全部加载）；用 `manifest.entry` 可只加载指定那一个。
- 清单文件名优先 `<插件名>.manifest.json`，找不到再找 `manifest.json`。
- 每个插件有一个独立数据目录：`<管理器目录>\plugin-data\<插件名>\`（`ctx.DataDir`）。

---

## 3. 契约：IManagerPlugin

命名空间 `UTSLManager.Plugins`（在 `UTSLManager.dll` 里）。

| 成员 | 类型 | 说明 |
|:--|:--|:--|
| `Name` | `string` | 显示名（右侧菜单 + 管理页） |
| `Icon` | `string` | 菜单图标，emoji 即可 |
| `Description` | `string` | 一句话说明 |
| `Author` | `string` | 作者；为空时回退 manifest |
| `RequiredApiVersion` | `int` | 要求的管理器 API 版本，默认 1；用 v2 能力请写 `ManagerApi.Version` |
| `OnLoad(IManagerContext)` | `void` | 加载时调用一次；抛异常只禁用本插件 |
| `OnUnload()` | `void` | 卸载/重载/关闭时调用（默认空实现）；保存状态、停后台任务 |
| `CreateView(IManagerContext)` | `Control?` | 中间区域界面；纯动作/服务型返回 `null`（默认） |

要求：**公开、非抽象、无参构造函数**的类。

---

## 4. 能力：IManagerContext

每个插件拿到的是**按插件隔离**的上下文：配置目录、命令、指标、权限、i18n 各自独立。

### 4.1 基本 / 日志 / REST / 世界

| 成员 | 签名 | 说明 |
|:--|:--|:--|
| `PluginName` | `string` | 插件名 |
| `PluginDir` | `string` | 插件 dll/csx 所在目录 |
| `DataDir` | `string` | 插件专属数据目录（自动创建） |
| `DeployRoot` / `RestBase` / `Token` | `string` | 部署根目录 / REST 基地址 / 令牌 |
| `Language` | `string` | 管理器插件语言（`zh-CN` / `en-US`） |
| `Permissions` | `IReadOnlyCollection<string>` | 本插件声明的权限（未声明=空集合=全部允许） |
| `Log(string)` | `void` | 往管理器终端写一行 |
| `RestGet` / `RestPost` | `Task<string>` | 访问 UTSL REST（自动带令牌，需 `rest` 权限） |
| `Worlds` | `IReadOnlyList<WorldInfo>` | 世界列表快照（`Name` / `Size` / `Running`） |

### 4.2 事件总线（P1-5）

~~~csharp
ctx.Events.ServerStateChanged += up => ctx.Log("服务端 " + (up ? "上线" : "下线"));
ctx.Events.WorldChanged        += w  => ctx.Log("切到世界 " + w);
ctx.Events.WorldListRefreshed  += list => ctx.Log("世界数 " + list.Count);
ctx.Events.MetricsSampled      += s  => { /* s.CpuPercent / s.MemoryGb / s.UpKbPerSec / s.DownKbPerSec */ };
ctx.Events.TerminalLine        += l  => { /* l.Text / l.Level —— 高频，注意性能 */ };
ctx.Events.ShuttingDown        += () => { /* 保存状态 */ };
~~~

| 事件 | 触发时机 | 备注 |
|:--|:--|:--|
| `ServerStateChanged(bool)` | 服务端上线/下线（REST 探测） | |
| `WorldChanged(string)` | 左侧世界列表选中项变化 | |
| `WorldListRefreshed(list)` | 世界列表内容变化 | |
| `MetricsSampled(MetricsSample)` | 约 2 秒一次 | 处理器要快，别阻塞 UI 线程 |
| `TerminalLine(TerminalLineEvent)` | 终端新增一行 | ☠ 高频（服务端日志每秒几十行）；没订阅时零开销 |
| `ShuttingDown()` | 管理器关闭 | 之后会调用 `OnUnload` |

### 4.3 终端命令（P1-6）

在管理器底部输入框敲 **`!名字 参数`** 触发（`/` 开头仍是发给服务端的 TShock 指令）。

~~~csharp
ctx.RegisterCommand("hello", "示例命令", args =>
{
    args.Reply("收到: " + args.RawArguments + "（世界 " + (args.TargetWorld ?? "全部") + "）");
    return Task.CompletedTask;
});
~~~

`PluginCommandArgs`：`Name` / `Args` / `RawArguments` / `TargetWorld` / `Context` / `Reply(text)`。
管理器内置 `!help` 会列出所有插件命令。

### 4.4 指标与状态（P1-10 / P2-15）

~~~csharp
ctx.RegisterMetric("我的插件 · 在线时长", "秒", () => _uptime.TotalSeconds);  // 数字，显示在「插件指标」区
ctx.RegisterStatus("我的插件 · 状态", () => _busy ? "忙" : "空闲");            // 文本状态胶囊
~~~

管理器每 2 秒刷新一次（拉取 `Provider()`）。`Provider` 要快、不要抛异常（抛了会显示 `—`）。

### 4.5 配置与存储（P1-7）

~~~csharp
public sealed class MyConfig { public int Count { get; set; } public string Name { get; set; } = ""; }

var cfg = ctx.GetConfig<MyConfig>();      // 缺省 <DataDir>\config.json，不存在返回默认值
cfg.Count++;
ctx.SaveConfig(cfg);
ctx.SaveConfig(other, "other.json");      // 也可以多文件

string text = ctx.ReadDeployText("config/EdgeStitch/EdgeStitch.json");   // 需 fs 权限
ctx.WriteDeployText("config/xxx.json", "{ }") ;                        // 路径被限制在 utsl-deploy 内
~~~

### 4.6 i18n（P2-14）

~~~csharp
ctx.RegisterStrings("zh-CN", new Dictionary<string, string> { ["hello"] = "你好" });
ctx.RegisterStrings("en-US", new Dictionary<string, string> { ["hello"] = "Hello" });
string s = ctx.L("hello");          // 当前语言 → 任一语言 → fallback → key
~~~

语言在「🧩 插件管理 → 插件语言」里切换（`zh-CN` / `en-US`，存进 `connection.json`）。

### 4.7 后台任务与关闭（P0-4）

~~~csharp
ctx.RunBackground(async ct =>
{
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(5000, ct);
        DoSomething();
    }
}, "我的轮询");
~~~

`RunBackground` 会捕获未处理异常写进终端，并在管理器关闭/插件卸载时取消（`ShutdownToken`）。

---

### 4.8 文件 / 命令 / HTTP（v3 新增）

**一句话**：这套文件 API 是「**统一部署目录访问**」—— 远端模式下走 SSH（列目录/读写）与 scp（上传下载），
本机模式下直接读盘；插件**不需要**判断自己连的是远端还是本机。

```csharp
// 1) 当前实际部署目录（远端模式=远端目录；本机模式=本机目录）
string root = ctx.FilesRoot;                      // 如 C:\utsl-deploy

// 2) 列目录（相对路径，空字符串 = 根）
foreach (var f in ctx.ListFiles("") ?? Array.Empty<PluginFileInfo>())
    ctx.Log($"{f.Name}  dir={f.IsDirectory}  {f.SizeText}  {f.Modified:MM-dd HH:mm}");

// 3) 读 / 写 / 存在 / 建目录 / 改名 / 删除（远端写为原子写：先 .tmp 再改名）
string cfg = ctx.ReadText("config/config.json");
bool w = ctx.WriteText("myplugin/notes.txt", "hello");
bool e = ctx.FileExists("myplugin/notes.txt");
ctx.CreateDirectory("myplugin");
ctx.Rename("myplugin/notes.txt", "notes2.txt");
ctx.Delete("myplugin/notes2.txt");

// 4) 本机 ⇄ 部署目录（远端走 scp）
ctx.Upload(@"C:\temp\a.zip", "backup/a.zip");
ctx.Download("config/config.json", @"C:\temp\cfg.json");

// 5) 改过文件后清缓存（宿主对远端读取有 90 秒缓存，不清会读到旧数据）
ctx.InvalidateFileCache();

// 6) 在部署主机上执行 PowerShell（需 exec 权限；远端走 ssh）
string out1 = await ctx.RunCommand("(Get-Process UnifierTSL).Count", 30000);

// 7) 任意 HTTP（需 net 权限）
string out2 = await ctx.HttpGet("https://example.com/api");
string out3 = await ctx.HttpPost("https://example.com/api", "{\"k\":1}");
```

**要点**
- 相对路径一律相对 `FilesRoot`，且**强制留在其内部**：`../..` 之类会抛 `IOException: 路径越界`（已实测拦截）。
- 所有远端调用都有成本（ssh 每次约 1.5~8 秒）→ 批量场景请缓存结果或合并调用。
- `Upload/Download` 的**本机**一侧路径由你自己决定；`Rename` 的 `newName` 只能是名字（不能带路径分隔符）。

---

## 5. 清单：manifest.json

~~~json
{
  "name": "我的插件",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "一句话说明",
  "entry": "MyPlugin.MyPlugin",
  "type": "view",
  "apiVersion": 2,
  "permissions": ["rest", "config", "fs", "exec", "net"],
  "dependencies": ["OtherPlugin@>=1.0.0"]
}
~~~

| 字段 | 必需 | 说明 |
|:--|:--|:--|
| `name` | 否 | 插件名；也用于依赖匹配与 UI |
| `version` | 否 | 版本；进加载日志（缺省显示 `v?`） |
| `author` | 否 | 作者；插件 `Author` 为空时回退 |
| `description` | 否 | 说明 |
| `entry` | 否 | 入口类型（全名或短名）；留空=扫描全部实现 |
| `type` | 否 | `view` / `action` / `service`，仅标注用途 |
| `apiVersion` | 否 | 要求的宿主 API 版本（缺省=1）；高于宿主会**拒绝加载并给出提示** |
| `permissions` | 否 | `rest` / `fs` / `config`；**不写=全部允许**，写 `[]`=全部禁止 |
| `dependencies` | 否 | 依赖的其他插件（按 name 或 dll 名匹配）；缺失/失败会跳过依赖者 |

清单缺失或 JSON 损坏都不影响加载（按“无清单”处理）。

---

## 6. 权限模型（P2-13）

| 权限 | 管什么 |
|:--|:--|
| `rest` | `RestGet` / `RestPost`（自动带令牌访问 UTSL REST） |
| `fs` | `ReadDeployText`/`WriteDeployText` 与 **v3 全部文件 API**（ListFiles/ReadText/WriteText/CreateDirectory/Rename/Delete/Upload/Download/InvalidateFileCache） |
| `config` | `GetConfig` / `SaveConfig` |
| `exec` | `RunCommand`（在部署主机执行 PowerShell，**能力很强，谨慎使用**） |
| `net` | `HttpGet` / `HttpPost`（访问任意 http(s)） |

- **不写 `permissions`** → 全部允许（向后兼容，老插件照常跑）。
- **写 `permissions: []`** → 全部禁止；调用受限 API 会抛 `PluginPermissionException`。
- `Log` / 事件 / 命令 / 指标 / `DataDir` 不需要权限。

> ⚠️ 诚实说明：这是**声明 + 对管理器提供 API 的强制**，**不是安全沙箱**。
> 插件同进程运行，仍可直接 `System.IO` 或 `HttpClient`。它的作用是「让作者声明意图 + 让用户知道边界 + 防误用」，
> 不要把它当成防恶意插件的机制；来源不可信的插件不要装。

---

## 7. 依赖与加载顺序（P1-9）

~~~json
{ "name": "MyPlugin", "dependencies": ["OtherPlugin"] }
~~~

- 依赖按 **name 或 dll 文件名**匹配（`OtherPlugin@>=1.0.0` 里的版本段目前不校验）。
- 管理器做**拓扑排序**：被依赖的先加载。
- 依赖缺失 → 依赖者被跳过并在「插件管理」页给出原因；依赖加载失败 → 依赖者同样跳过。
- 循环依赖会被检测出来并标记失败。

---

## 8. 脚本插件：`.csx`（P2-12）

不想建工程？直接写脚本。Roslyn 会把它编译成程序集，然后**走和 dll 插件完全相同的流程**（清单 / API 校验 / 依赖 / 可回收 ALC）。

~~~csharp
// plugins/hello.csx
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

同目录再放 `hello.manifest.json`：

~~~json
{ "name": "脚本示例", "version": "1.0.0", "entry": "ScriptHello", "apiVersion": 2 }
~~~

注意：`entry` 可以写短名（脚本里的类型真名是 `Submission#0+ScriptHello`，短名匹配即可）。
脚本编译失败时，「插件管理」页会显示诊断（`CS####: 消息`）。
可用的引用：`UTSLManager`、`Avalonia` / `Avalonia.Controls` / `Avalonia.Base`，以及 BCL；自动 import 了 `UTSLManager.Plugins` / `Avalonia*` / `System*`。

用法：把 `hello.csx` 与 `hello.manifest.json` 一起放进 `plugins\\`（同名清单即可）。

---

## 9. 加载流程与错误隔离

~~~
管理器启动（或点「重新加载」）
  └─ PluginHost.Load()
       ├─ 卸载旧的一代（OnUnload → 清空命令/指标/订阅 → ALC.Unload）
       └─ PluginLoader.Load()
            ├─ 扫描 plugins\（扁平 + 一插件一目录，*.dll / *.csx）
            ├─ 读 <名>.manifest.json（有就用）
            ├─ 校验 apiVersion / 依赖（拓扑排序）
            ├─ dll → 可回收 ALC 从内存加载；csx → Roslyn 编译后同路
            ├─ 找 IManagerPlugin 实现（或 manifest.entry 指定）
            ├─ 创建实例 → OnLoad(ctx)
            └─ 成功项进「扩展」菜单；失败项进 PluginLoadReport
~~~

**三重隔离**：
1. 单个类初始化失败 → 跳过该类，其余继续；
2. 单个文件加载失败（坏 IL / 缺依赖 / API 不符） → 跳过该文件，管理器照常启动；
3. `CreateView` 抛异常 → 插件页显示红字错误，管理器不崩。

`OnLoad`/`OnUnload`/`CreateView` 有兜底；**事件处理器与命令处理器也有兜底**（异常写终端）。
但插件自己的 UI 按钮回调请自行 `try/catch`。

### 「🧩 插件管理」页（P0-2 / P0-3）

- 已加载插件：图标 / 名称 / 版本 / API / 类型 / 作者 / 来源文件 / 权限 / 命令，附「停用」按钮；
- **加载失败列表**：文件 + 具体原因（缺依赖 / API 版本 / 编译错误 / 坏 IL …）；
- 已停用 / 未加载文件：一键启用或停用；
- 顶部：插件目录路径、`已加载 / 失败 / 扫描` 计数、宿主 API 版本、**重新加载**、**打开插件目录**、目录监视开关、插件语言。

> 找不到插件时**先看这里**，不要只看终端。

---

## 10. 热重载与卸载（P1-8）

- 每个插件（含脚本）加载进**独立的可回收 `AssemblyLoadContext`**；宿主已有的程序集（`UTSLManager` / `Avalonia*`）**共享**，所以插件类型和宿主类型是同一个类型。
- dll 一律**从内存加载**，文件不会被锁 → 管理器跑着也能重新编译/覆盖插件。
- 「重新加载」= 卸载全部（`OnUnload` + ALC.Unload）→ 重新扫描加载。
- 勾选「监视目录自动重载」后，`plugins\` 里 dll / manifest 变化会在 0.9 秒防抖后自动重载（默认关闭，避免边编译边重载）。

⚠️ 卸载要真正回收，需保证管理器不再引用插件对象：管理器会清空「扩展」菜单、插件视图缓存与所有订阅。
所以**插件不要在静态字段里长期持有界面控件或管理器对象**；自定义 `Control` 子类要从可视树摘掉（管理器已做）。

---

## 11. 写界面（Avalonia）

- 推荐**纯 C# 建控件**（示例就是），插件工程最简单；也可自带 `.axaml`，但要自己配好 `AvaloniaResource`，管理器不会帮你编译 XAML。
- 界面**会被缓存**（同一插件切走再切回不重建），可以在字段里保存控件引用、就地更新文本。
- 配色对齐管理器深色主题：

| 用途 | 颜色 |
|:--|:--|
| 主文字 | `#F2F2F5` |
| 次要 | `#B9B9C0` |
| 弱化 | `#7C7C82` / `#6E7681` |
| 强调 | `#66CCFF` |
| 错误 | `#FF7B72` |
| 卡片底 / 描边 | `#141417` / `#2A2A30` |

- 不要在 UI 线程做阻塞操作（同步 `HttpClient`、`Thread.Sleep`、大文件 IO）；用 `await` 或 `RunBackground`。

---

## 12. 硬性规则（踩过坑）

1. **不要把 `UTSLManager.dll` 或任何 `Avalonia*.dll` 复制进 `plugins\`。**
   引用必须 `<Private>false</Private>`（并 `CopyLocalLockFileAssemblies=false`）。
   第二份同名程序集会破坏类型统一 → `IsAssignableFrom` 为 false → **静默不加载**。
2. **Avalonia 版本必须与管理器一致**（当前 `12.1.2`，宿主里钉死）；浮动 `12.*` 可能拉到不兼容版。
3. **`TargetFramework` 必须与管理器一致**（当前 `net10.0`）。
4. 停用插件**不要删文件**，改名 `X.dll.disabled` / `X.csx.disabled`（加载器只扫 `.dll` / `.csx`）。
5. `OnLoad` 时服务端可能还没起来 → 别在 `OnLoad` 里依赖 REST / 世界列表。
6. `TerminalLine` 事件是高频流（服务端日志），处理器要极快，别在里面做 IO。
7. 用了第三方 NuGet 依赖的插件请用**一插件一目录**布局，把依赖 dll 一起放进去（ALC 会按 `.deps.json` / 目录解析）。

---


## 12.5 Lua 插件（v6 —— 社区里有 Lua 作者，所以专门支持）

放 `plugins\xxx.lua`（扁平）或 `plugins\<名>\plugin.lua`（推荐，可带同名 `plugin.manifest.json`，与 dll 插件**同一套**清单）。
顶层直接写代码即可，宿主把 `ctx` 注入为全局变量；`ctx:meta{...}` 可覆盖插件名/图标/说明/作者。

```lua
ctx:meta{ name = "我的 Lua 插件", icon = "🎐", description = "一句话", author = "你" }

ctx:log("加载完成")
ctx:every(60, function() ctx:log("每分钟一次：" .. ctx:now().str) end)   -- 计时器（后台线程执行）
```

**ctx 能力一览**（与 C# 插件的 `IManagerContext` 一一对应）：

| Lua | 说明 |
|---|---|
| `ctx:log(s)` | 写进管理器终端 |
| `ctx:now()` | `{year,month,day,hour,min,sec,wday,epoch,str}`（`wday` 1=周一…7=周日；所以 `os` 可以整个禁掉） |
| `ctx:every(sec, fn)` / `ctx:after(sec, fn)` / `ctx:cancel(id)` | 周期 / 一次性 / 取消（**回调都在后台线程**，见下方安全边界） |
| `ctx:get_config()` / `ctx:save_config(t)` | Lua 表 ↔ `<DataRoot>\<插件名>\config.json`（中文按原样写，方便手改） |
| `ctx:register_command(name, desc, fn)` | 终端里 `!name 参数` 触发；`fn(raw, world)` |
| `ctx:register_metric(name, unit, fn)` / `ctx:register_status(name, fn)` | 右侧「插件指标」区（宿主会**从后台线程**调用，别做慢操作） |
| `ctx:rest_get(url)` / `ctx:rest_post(url)` | 当前连接的 UTSL REST（自动带令牌，需 `rest` 权限） |
| `ctx:run_command(ps, timeoutMs)` | 在部署主机执行 PowerShell（需 `exec` 权限，**能力很强**） |
| `ctx:read_text/ write_text/ exists/ list_files` | 部署目录文件（需 `fs` 权限；远端/本机统一） |
| `ctx:worlds()` | 世界列表快照 |
| `ctx:L(key, fallback)` | i18n |
| `ctx:server_online()` | 服务端是否在线（管理器**心跳探测**结果，本机/远端同一语义） |
| `ctx:server_start(reason)` / `ctx:server_stop(reason)` | **启停服务端进程**（需 `exec` 权限；走的是与管理器按钮**同一条**路径 —— 远端=SSH/控制台启动器；`reason` 会写进终端便于审计） |
| `ctx:register_panel(title, fn)` + `ctx:on_action(fn)` | **声明式界面**（见下） |

**界面是"声明式"的**：Lua **不能**返回 Avalonia 控件（那会踩类型身份分裂），而是返回一份控件描述，宿主渲染**真控件**。
控件种类：`text`（只读文本）/ `text_input` / `number` / `checkbox`（真复选框）/ `select`（下拉）/ **`time`（时间选择器，值 `"HH:mm"`）** /
**`date`（日历选日期，值 `"yyyy-MM-dd"`）** / **`list`（可点选列表，点击回调 `action="select"` 且 `values.clicked` 是被点项的 `key`）** /
`buttons` / `section`（分节标题）/ `hint`（灰色说明行）/ `divider`。整体按"**标签左、控件右**"的两列布局渲染。

```lua
ctx:register_panel("我的面板", function()
  return {
    { kind = "text",   label = "状态", value = "运行中" },
    { kind = "select", id = "mode", label = "模式", options = { "间隔", "每天" }, value = "间隔" },
    { kind = "number", id = "every", label = "间隔(分钟)", value = 30 },
    { kind = "buttons", items = { { text = "保存", action = "save" } } },
  }
end)
ctx:on_action(function(action, values)   -- values.mode / values.every … 就是上面各控件当前值
  ctx:log("点了 " .. action .. "，间隔=" .. tostring(values.every))
end)
```

**安全边界（实测，探针 `probes/LuaSpike`）**：用 MoonSharp 官方**软沙箱**，并在宿主里**额外剔除** `os`、`package`
（实测软沙箱仍保留这两个）；`io / load / loadstring / dofile / require / debug` 均不可用。
宿主**不注册任何 .NET 类型**给 Lua，唯一的通道就是 `ctx` 上的那些函数。

★ **诚实的边界**：MoonSharp 没有内建指令计数 → 写错的死循环会**占满一个 CPU 核**；
  但所有 Lua 回调都在**后台线程**执行（实测：Lua 死循环期间管理器计时器照常跳动），所以**界面不会被冻住**。
  这与"C# 插件本来就能 `RunCommand` 执行任意 PowerShell"是同一级信任模型：**插件是你自己装的，就该信任它**。

**完整示例：`extensions\LuaTaskScheduler`（定时任务）** —— 排程：**间隔 N 秒** / 每天 HH:mm / 每周几 / 每月几号 / **指定日期（日历选）**；
动作：**REST（默认）** / PowerShell 命令 / **启动服务端** / **停止服务端**；选项：**仅服务器在线时执行**，
并带 **10 分钟宽限补跑**（04:00 恰好离线、04:05 才上线也会补跑一次，不会永久错过）；
界面是**可直接点选的任务列表** + 两列表单 + 最近执行记录（保存 / 立即执行 / 新增 / 复制 / 删除）。
它的排程判定与动作分派都是纯函数/可注入并**导出给自动化测试**：`probes/LuaSchedCheck` 会加载**真实发布的那份 plugin.lua**，
用桩 ctx（含记录型桩）跑 **28 个用例**（秒级间隔边界 / 宽限补跑 / 每周 / 每月 / 指定日期 / 描述文案 /
**动作分派是否真的调到 rest·cmd·start·stop**）——**当前 28/28 通过**。
另有 `extensions\LuaSmoke`（自检插件）：把"加载次数/心跳次数"写进配置，可**从磁盘客观验证** Lua 通道是否工作。
## 13. 示例

| 示例 | 位置 | 演示内容 |
|:--|:--|:--|
| **正式插件（推荐参考）** | `extensions/PacketInspector/` | 独立 csproj 引用 UTSLManager.dll、清单、REST 轮询、命令、指标、状态、规则工具栏、按需解码/限流/封包 —— 一个完整可跑的插件 |

实测加载日志（`extensions/PacketInspector`）：

~~~
[插件] 已加载 📡 数据包查看器 v1.0.0 [view]（PacketInspector.dll，作者 Zykor-Club）
[插件] 共加载 1 个管理器插件
~~~

---

## 14. 排错 FAQ

**Q：右侧「扩展」没有我的插件。**
1. 打开「🧩 插件管理」看**失败列表**（原因都写在那里）；
2. 确认文件在 `<UTSLManager.exe 目录>\plugins\`（**不是** `utsl-deploy\plugins`）；
3. 确认类 `public`、非抽象、有无参构造；
4. 确认 `plugins\` 里没有混入 `UTSLManager.dll` / `Avalonia*.dll`（最常见的静默失败）；
5. 点「重新加载」。

**Q：提示 `需要 API v2，本管理器支持到 v2` 之类的版本问题。**
→ 插件要求的 API 版本高于宿主：更新管理器，或把 `apiVersion` / `RequiredApiVersion` 降下来。

**Q：`TypeLoadException` / `FileNotFoundException: Avalonia.Controls`。**
→ `plugins\` 里混进了旧版或重复的 Avalonia；删掉，只留插件自己的文件。

**Q：`.csx` 加载失败。**
→ 看「插件管理」的编译诊断；常见是缺少 `using`、类型名与 `entry` 对不上、用了未引用的包。

**Q：插件命令没反应。**
→ 命令要用 `!名字` 触发（`/` 是发给服务端的）；用 `!help` 看已注册命令；命令是异步执行的，结果会稍后打在终端。

**Q：改了代码不生效。**
→ 没 build / 没复制 / 没重载。管理器运行时文件不再被锁，可以直接覆盖；点「重新加载」或开目录监视即可。

**Q：REST 调用失败。**
→ 服务端没起、端口/令牌不对（「连接设置」），或没装 `WorldCommandChannel`；也要确认 manifest 里声明了 `rest` 权限。

---

## 15. 实现位置（想读源码时）

| 文件 | 作用 |
|:--|:--|
| `Plugins/IManagerPlugin.cs` | `IManagerPlugin` + `IManagerContext` 契约 |
| `Plugins/ManagerApi.cs` | API 版本、清单、描述符、加载报告等模型 |
| `Plugins/ManagerEvents.cs` | 事件总线（每次重载换新实例） |
| `Plugins/PluginLoadContext.cs` | 可回收 ALC（共享宿主程序集、内存加载、私有依赖解析） |
| `Plugins/PluginLoader.cs` | 扫描 / 清单 / 依赖排序 / dll 与 .csx 加载 / 错误隔离 |
| `Plugins/PluginHost.cs` | 宿主：生命周期、命令、指标、状态、权限、配置、i18n |
| `MainWindow.axaml(.cs)` | 「扩展」菜单、插件视图、插件管理页、事件触发、`!命令` 派发 |

