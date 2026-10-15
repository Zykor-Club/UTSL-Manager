# get-tedit.ps1 —— 把 TEdit 源码取到 third_party\TEdit（钉死一个 commit，保证可复现）
#
# 为什么不用 git submodule：
#   管理器是公开仓库，而 TEdit 只服务于「世界地图」这一个功能。做成 submodule 会逼所有克隆者
#   （含 CI、以及不关心地图的人）去拉一个几十 MB 的外部仓库。改成"按需 clone + 钉 commit"，
#   主仓库保持自足，同时构建仍然可复现。
#
# ★ 这是【构建前置步骤】：管理器本体直接编译 TEdit 的渲染核心
#   （UTSLManager.Avalonia.csproj 里有 ProjectReference + 挑编 PixelMap/SkiaTEditExtensions），
#   所以 dotnet restore/build 之前必须先把这个脚本跑过。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\get-tedit.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\get-tedit.ps1 -Force     # 已存在也重新拉

param(
    [string]$Target = "",
    [string]$Repo   = "https://github.com/TEdit/Terraria-Map-Editor.git",
    [string]$Commit = "ce380317edb61211ee568d6fcda221e2b5321a62",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Target)) {
    $root = Split-Path -Parent $PSScriptRoot          # 仓库根
    $Target = Join-Path (Join-Path $root "third_party") "TEdit"
}
$Target = [IO.Path]::GetFullPath($Target)

Write-Host "[get-tedit] 目标目录: $Target"
Write-Host "[get-tedit] 仓库:     $Repo"
Write-Host "[get-tedit] 钉死提交: $Commit"

if (Test-Path (Join-Path $Target ".git")) {
    if (-not $Force) {
        Write-Host "[get-tedit] 已存在 .git，跳过 clone。要强制重拉请加 -Force"
    } else {
        Write-Host "[get-tedit] -Force：删除已有目录"
        Remove-Item -LiteralPath $Target -Recurse -Force
    }
}

if (-not (Test-Path (Join-Path $Target ".git"))) {
    $parent = Split-Path -Parent $Target
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    # 只拉需要的分支，浅克隆后再补齐目标 commit
    git clone --filter=blob:none --no-checkout $Repo $Target
    if ($LASTEXITCODE -ne 0) { throw "git clone 失败" }
}

Push-Location $Target
try {
    # 快路径：已经是指定 commit 就不联网（本地反复构建、CI 缓存命中都走这里）
    $cur = ""
    try { $cur = (git rev-parse HEAD 2>$null).Trim() } catch { }
    if ($cur -eq $Commit) {
        Write-Host "[get-tedit] HEAD 已经是指定 commit，跳过 fetch/checkout"
    }
    else {
        git fetch --depth 1 origin $Commit
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[get-tedit] 浅拉该 commit 失败，改为 full fetch"
            git fetch origin
            if ($LASTEXITCODE -ne 0) { throw "git fetch 失败" }
        }
        git checkout --detach $Commit
        if ($LASTEXITCODE -ne 0) { throw "git checkout $Commit 失败" }
    }

    $head = (git rev-parse HEAD).Trim()
    Write-Host "[get-tedit] 已就位 HEAD = $head"

    $need = @(
        "src\TEdit.Terraria\TEdit.Terraria.csproj",
        "src\TEdit5\Controls\PixelMap.cs",
        "src\TEdit5\Controls\WorldRenderEngine\SkiaTEditExtensions.cs",
        "src\TEdit5\Controls\WorldRenderEngine\Layers\RenderLayerVisibility.cs"
    )
    foreach ($f in $need) {
        if (-not (Test-Path (Join-Path $Target $f))) { throw "TEdit 源码缺少 $f（上游可能改了结构，请核对该 commit）" }
    }
    Write-Host "[get-tedit] 渲染所需的 4 个源文件都在。"
}
finally {
    Pop-Location
}
