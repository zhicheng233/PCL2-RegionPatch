# 校验 payload\ 里的 3 个交付文件
#   - 存在且非空
#   - 两个 DLL 是合法的托管程序集，且程序集名正确
#   - config 能解析，关键节点齐全（尤其别把长路径开关那行删掉）
$ErrorActionPreference = 'Stop'

$payload = Join-Path (Split-Path -Parent $PSScriptRoot) 'payload'
if (-not (Test-Path $payload)) { throw "payload 目录不存在：$payload" }

$expect = @(
    [pscustomobject]@{ Name = 'PclRuntime.dll';                    Assembly = 'PclRuntime' },
    [pscustomobject]@{ Name = '0Harmony.dll';                      Assembly = '0Harmony'   },
    [pscustomobject]@{ Name = 'Plain Craft Launcher 2.exe.config'; Assembly = $null        }
)

Write-Host '--- 文件 ---'
foreach ($e in $expect) {
    $p = Join-Path $payload $e.Name
    if (-not (Test-Path $p)) { throw "缺少文件：$($e.Name)" }
    $len = (Get-Item $p).Length
    if ($len -le 0) { throw "文件为空：$($e.Name)" }
    Write-Host ("  OK  {0,-34} {1,9} bytes" -f $e.Name, $len)

    if ($e.Assembly) {
        $an = [System.Reflection.AssemblyName]::GetAssemblyName($p)
        if ($an.Name -ne $e.Assembly) {
            throw "$($e.Name) 的程序集名是 '$($an.Name)'，期望 '$($e.Assembly)'"
        }
        Write-Host ("      assembly = {0}, Version={1}" -f $an.Name, $an.Version)
    }
}

Write-Host '--- config ---'
$cfgPath = Join-Path $payload 'Plain Craft Launcher 2.exe.config'
[xml]$cfg = Get-Content $cfgPath -Raw

$required = @(
    '/configuration/runtime/appDomainManagerAssembly',
    '/configuration/runtime/appDomainManagerType',
    '/configuration/runtime/AppContextSwitchOverrides'
)
foreach ($xpath in $required) {
    if (-not $cfg.SelectSingleNode($xpath)) { throw "config 缺少节点：$xpath" }
    Write-Host "  OK  $xpath"
}

$typeNode = $cfg.SelectSingleNode('/configuration/runtime/appDomainManagerType')
if ($typeNode.value -ne 'PclRuntime.PclRuntimeManager') {
    throw "appDomainManagerType 是 '$($typeNode.value)'，期望 'PclRuntime.PclRuntimeManager'"
}

$asmNode = $cfg.SelectSingleNode('/configuration/runtime/appDomainManagerAssembly')
if ($asmNode.value -notlike 'PclRuntime*') {
    throw "appDomainManagerAssembly 是 '$($asmNode.value)'，期望以 'PclRuntime' 开头"
}

# 这一行如果丢了，PCL 启动会误报"没有对当前文件夹的权限"
$switchNode = $cfg.SelectSingleNode('/configuration/runtime/AppContextSwitchOverrides')
foreach ($s in @('Switch.System.IO.UseLegacyPathHandling=false', 'Switch.System.IO.BlockLongPaths=false')) {
    if ($switchNode.value -notlike "*$s*") {
        throw "AppContextSwitchOverrides 缺少 '$s'（会导致 PCL 启动报权限错误）"
    }
}
Write-Host '  OK  AppContextSwitchOverrides 两个长路径开关都在'

Write-Host ''
Write-Host '校验通过。' -ForegroundColor Green
