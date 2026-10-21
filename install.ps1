# 把运行时补丁部署到 PCL 目录（不修改 exe）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File install.ps1
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Target "D:\Games\PCL"
#
# 默认目标是本文件夹的上一级目录（即把本包放进 PCL 目录后再执行）。
param(
    [string]$Target = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$exeName  = 'Plain Craft Launcher 2.exe'
$payload  = Join-Path $PSScriptRoot 'payload'
$files    = @('Plain Craft Launcher 2.exe.config', 'PclRuntime.dll', '0Harmony.dll')

# --- 校验目标 ---
if (-not (Test-Path $Target -PathType Container)) {
    throw "目标目录不存在：$Target"
}
$Target = (Resolve-Path $Target).Path
if (-not (Test-Path (Join-Path $Target $exeName))) {
    throw "目标目录里没有 '$exeName'：$Target`n请用 -Target 指定 PCL 所在目录。"
}
foreach ($f in $files) {
    if (-not (Test-Path (Join-Path $payload $f))) {
        throw "payload 缺少文件：$f（先运行 build.ps1）"
    }
}

Write-Host "目标目录：$Target" -ForegroundColor Cyan

# --- PCL 正在运行时提醒 ---
$running = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "警告：PCL 正在运行，补丁要重启后才生效。" -ForegroundColor Yellow
}

# --- 备份非本补丁的既有 config ---
$cfg     = Join-Path $Target $files[0]
$newCfg  = Join-Path $payload $files[0]
if (Test-Path $cfg) {
    $oldHash = (Get-FileHash $cfg -Algorithm SHA256).Hash
    $newHash = (Get-FileHash $newCfg -Algorithm SHA256).Hash
    if ($oldHash -ne $newHash) {
        Copy-Item $cfg "$cfg.bak" -Force
        Write-Host "已备份原有配置 -> $cfg.bak" -ForegroundColor Yellow
    }
}

# --- 复制 ---
foreach ($f in $files) {
    Copy-Item (Join-Path $payload $f) (Join-Path $Target $f) -Force
    Write-Host "  已写入 $f" -ForegroundColor Green
}

Write-Host ""
Write-Host "部署完成。直接启动 PCL 即可，exe 未被修改。" -ForegroundColor Green
Write-Host "补丁日志：$env:TEMP\PclRuntime.log"
Write-Host "卸载：powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Target `"$Target`""
