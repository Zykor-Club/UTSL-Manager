<#
.SYNOPSIS
  把 UTSLDaemon 发布成【自包含单文件】并放进管理器的发布目录（agent/UTSLDaemon.exe）。

.DESCRIPTION
  管理器的「连接设置 → 安装/更新云端 agent」按钮就是把这个文件 scp 到被管机器、再建计划任务。
  为什么自包含：被管机器上通常没有 .NET 10（实测远端只有 .NET 9），框架依赖版跑不起来。

.EXAMPLE
  ./scripts/publish-agent.ps1                       # 输出到仓库上两级\AvaloniaUTSL\agent\
  ./scripts/publish-agent.ps1 -ManagerDir D:\app   # 指定管理器目录
#>
param(
    [string]$ManagerDir = (Join-Path $PSScriptRoot "..\..\AvaloniaUTSL")
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$proj = Join-Path $repo "daemon\UTSLDaemon\UTSLDaemon.csproj"
$out  = Join-Path $ManagerDir "agent"

if (-not (Test-Path $proj)) { throw "找不到 daemon 工程: $proj" }
Write-Host "发布 UTSLDaemon（自包含单文件 win-x64）→ $out"
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish 失败（exit=$LASTEXITCODE）" }

$exe = Join-Path $out "UTSLDaemon.exe"
$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "✓ $exe  ($mb MB)"
