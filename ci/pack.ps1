# 把 5 个文件打成一个 zip（Release 附件）
#
#   payload.zip
#   └─ payload\
#      ├─ PclRuntime.dll
#      ├─ 0Harmony.dll
#      ├─ Plain Craft Launcher 2.exe.config
#      ├─ install.ps1
#      └─ uninstall.ps1
#
# 解压到 PCL 目录后执行 .\payload\install.ps1 即可（脚本会自动识别上一级是 PCL 目录）。
param(
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutFile) { $OutFile = Join-Path $root 'dist\payload.zip' }

$files = @(
    'payload\PclRuntime.dll',
    'payload\0Harmony.dll',
    'payload\Plain Craft Launcher 2.exe.config',
    'install.ps1',
    'uninstall.ps1'
)

# --- 先确认源文件都在，避免打出一个残缺的包 ---
foreach ($f in $files) {
    $src = Join-Path $root $f
    if (-not (Test-Path $src)) { throw "缺少文件：$f（先运行 build.ps1）" }
    if ((Get-Item $src).Length -le 0) { throw "文件为空：$f" }
}

# --- 在临时目录里摆好 payload\ 结构再压缩 ---
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('pcl2-pack-' + [guid]::NewGuid().ToString('N'))
$inner = Join-Path $stage 'payload'
New-Item -ItemType Directory -Force -Path $inner | Out-Null

try {
    foreach ($f in $files) {
        Copy-Item (Join-Path $root $f) (Join-Path $inner (Split-Path -Leaf $f)) -Force
    }

    $outDir = Split-Path -Parent $OutFile
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    if (Test-Path $OutFile) { Remove-Item $OutFile -Force }

    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $OutFile -CompressionLevel Optimal
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# --- 回读校验：条目数、顶层文件夹名 ---
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($OutFile)
try {
    $entries = @($zip.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName })
} finally {
    $zip.Dispose()
}

if ($entries.Count -ne $files.Count) {
    throw "zip 内条目数是 $($entries.Count)，期望 $($files.Count)：`n$($entries -join "`n")"
}
foreach ($e in $entries) {
    if ($e -notlike 'payload/*') { throw "zip 内路径不符合预期：$e" }
}

$item = Get-Item $OutFile
$hash = (Get-FileHash $OutFile -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ''
Write-Host "已生成：$($item.FullName)" -ForegroundColor Green
Write-Host ("  大小   : {0} bytes" -f $item.Length)
Write-Host ("  SHA256 : {0}" -f $hash)
Write-Host '  内容   :'
foreach ($e in $entries) { Write-Host "    $e" }
