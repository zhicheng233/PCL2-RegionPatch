# 卸载运行时补丁（删除 sidecar 文件，PCL 回到原版行为）
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File uninstall.ps1
#   powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Target "D:\Games\PCL"
param(
    [string]$Target = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$exeName = 'Plain Craft Launcher 2.exe'
$files   = @('Plain Craft Launcher 2.exe.config', 'PclRuntime.dll', '0Harmony.dll')

if (-not (Test-Path $Target -PathType Container)) {
    throw "目标目录不存在：$Target"
}
$Target = (Resolve-Path $Target).Path

$running = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "警告：PCL 正在运行，建议先关闭。" -ForegroundColor Yellow
}

Write-Host "目标目录：$Target" -ForegroundColor Cyan

foreach ($f in $files) {
    $p = Join-Path $Target $f
    if (Test-Path $p) {
        Remove-Item $p -Force
        Write-Host "  已删除 $f" -ForegroundColor Green
    } else {
        Write-Host "  跳过（不存在）$f" -ForegroundColor DarkGray
    }
}

# 还原 install.ps1 备份的配置
$bak = Join-Path $Target ($files[0] + '.bak')
if (Test-Path $bak) {
    Move-Item $bak (Join-Path $Target $files[0]) -Force
    Write-Host "已还原备份配置 $($files[0])" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "卸载完成，PCL 已恢复原版。" -ForegroundColor Green
