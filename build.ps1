# 构建 PclRuntime.dll，并把产物刷新到 payload\
# 用法： powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'

$pkg  = $PSScriptRoot
$proj = Join-Path $pkg 'src\PclRuntime.csproj'
$out  = Join-Path $pkg 'src\bin\Release'
$pay  = Join-Path $pkg 'payload'

Write-Host "[1/3] 构建 $proj" -ForegroundColor Cyan
dotnet build $proj -c Release
if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }

Write-Host "[2/3] 复制产物到 payload\" -ForegroundColor Cyan
Copy-Item (Join-Path $out 'PclRuntime.dll') $pay -Force
Copy-Item (Join-Path $out '0Harmony.dll')   $pay -Force

Write-Host "[3/3] 产物校验" -ForegroundColor Cyan
Get-ChildItem $pay -Filter *.dll | ForEach-Object {
    "{0,-16} {1,9} bytes  {2}" -f $_.Name, $_.Length, (Get-FileHash $_.FullName -Algorithm SHA256).Hash
}
Write-Host "完成。用 install.ps1 部署到 PCL 目录。" -ForegroundColor Green
