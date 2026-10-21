# 生成 Release 正文：
#   1) 上一个 tag → 本次 tag 之间的全部提交
#   2) payload\ 里 3 个交付文件的 SHA-256
# 需要完整 git 历史（actions/checkout 的 fetch-depth: 0）。
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$PayloadDir,
    [string]$OutFile
)
$ErrorActionPreference = 'Stop'

# 让 PowerShell 解码原生命令输出时用 UTF-8，否则中文提交信息在 PS 5.1 下会乱码
# （PS 7 默认就是 UTF-8，这里是兼容 5.1）。
$utf8 = New-Object System.Text.UTF8Encoding $false
try { [Console]::OutputEncoding = $utf8 } catch { }
$OutputEncoding = $utf8

# 以脚本所在目录的上一级为仓库/项目根，这样在任何 CWD 下都能跑
$repo = Split-Path -Parent $PSScriptRoot
if (-not $PayloadDir) { $PayloadDir = Join-Path $repo 'payload' }
if (-not $OutFile)    { $OutFile    = Join-Path $repo 'release-body.md' }

$files = @('PclRuntime.dll', '0Harmony.dll', 'Plain Craft Launcher 2.exe.config')

# git 失败时会往 stderr 写东西；在 ErrorActionPreference=Stop 下会被当成异常。
# 统一走这个包装：合并 stderr、只取 stdout 文本行，并用 ExitCode 判断成败。
function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)

    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # i18n.logOutputEncoding 保证 log 输出固定是 UTF-8，不受机器 git config 影响
        $stdout = & git -C $repo -c core.quotepath=false -c i18n.logOutputEncoding=UTF-8 @GitArgs 2>&1 |
                  Where-Object { $_ -is [string] }
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = @($stdout)
        }
    } finally {
        $ErrorActionPreference = $old
    }
}

# ---------- 确认 tag 存在 ----------
$r = Invoke-Git rev-parse -q --verify "refs/tags/$Tag"
if ($r.ExitCode -ne 0) {
    throw "仓库里找不到 tag '$Tag'。checkout 时请使用 fetch-depth: 0 与 fetch-tags: true。"
}

# ---------- 定位上一个 tag ----------
$prevTag = $null
$hasParent = (Invoke-Git rev-parse -q --verify "$Tag^").ExitCode -eq 0
if ($hasParent) {
    $r = Invoke-Git describe --tags --abbrev=0 "$Tag^"
    if ($r.ExitCode -eq 0 -and $r.Output.Count -gt 0) {
        $prevTag = $r.Output[0].Trim()
    }
}

if ($prevTag) {
    $range     = "$prevTag..$Tag"
    $rangeText = "自 ``$prevTag`` 起的提交"
} else {
    $range     = $Tag
    $rangeText = '首次发布，包含全部提交'
}

# ---------- 收集提交 ----------
$commits = @()
$r = Invoke-Git log $range --no-merges --reverse '--pretty=%h%x09%s'
foreach ($line in $r.Output) {
    if ($line -match '^([0-9a-f]{7,40})\t(.*)$') {
        $commits += "- $($Matches[2]) (``$($Matches[1])``)"
    } elseif ($line.Trim()) {
        $commits += "- $line"
    }
}
if ($commits.Count -eq 0) { $commits = @('- （没有新提交）') }

# ---------- 计算校验和 ----------
$rows = @()
foreach ($f in $files) {
    $p = Join-Path $PayloadDir $f
    if (-not (Test-Path $p)) { throw "缺少产物：$p" }
    $rows += [pscustomobject]@{
        Name = $f
        Size = (Get-Item $p).Length
        Hash = (Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

# ---------- 拼装 markdown ----------
$md = [System.Collections.Generic.List[string]]::new()
$md.Add('## 变更内容')
$md.Add('')
$md.Add("$rangeText：")
$md.Add('')
foreach ($c in $commits) { $md.Add($c) }
$md.Add('')

if ($env:GITHUB_REPOSITORY -and $prevTag) {
    $md.Add("完整对比：$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/compare/$prevTag...$Tag")
    $md.Add('')
}

$md.Add('## 文件校验（SHA-256）')
$md.Add('')
$md.Add('| 文件 | 大小 | SHA-256 |')
$md.Add('| --- | ---: | --- |')
foreach ($r2 in $rows) {
    $md.Add('| `' + $r2.Name + '` | ' + $r2.Size + ' B | `' + $r2.Hash + '` |')
}
$md.Add('')

$md.Add('## 安装')
$md.Add('')
$md.Add('把上面三个文件放到 PCL 目录（与 `Plain Craft Launcher 2.exe` 同级），启动即可。')
$md.Add('**不需要修改 exe**，PCL 自动更新替换 exe 后补丁依然生效。')
$md.Add('')
$md.Add('```powershell')
$md.Add('powershell -ExecutionPolicy Bypass -File install.ps1 -Target "<PCL 目录>"')
$md.Add('```')
$md.Add('')
$md.Add('卸载：')
$md.Add('')
$md.Add('```powershell')
$md.Add('powershell -ExecutionPolicy Bypass -File uninstall.ps1 -Target "<PCL 目录>"')
$md.Add('```')
$md.Add('')
$md.Add('校验补丁是否生效：查看 `%TEMP%\PclRuntime.log`，应出现 5 组 `patched` 日志（共 9 处）。')

$md -join "`n" | Out-File -FilePath $OutFile -Encoding utf8
Write-Host "已生成 $OutFile" -ForegroundColor Green
Write-Host ''
Get-Content $OutFile
