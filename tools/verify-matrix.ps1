#Requires -Version 7.0
<#
.SYNOPSIS
    校验 README 平台矩阵里的 `ci:<job>` 引用都指向真实存在的 CI job。

.DESCRIPTION
    平台矩阵里的每一行状态都是一个**承诺**。承诺与仓库证据脱节时，最坏情况不是"少测了一个平台"，
    而是"以为测过了"——2026-10-23 的评审就撞上过一次：README 写着 Linux arm64"xvfb 冒烟"，
    而 ci.yml 里只有一条 IL 交叉编译。那句话看起来像证据，实际没有任何东西能复现它。

    所以把"证据"变成一个可核对的名字：README 写 `ci:smoke-linux-arm64`，本脚本确认
    ci.yml 里真有这个 job。引用错了 CI 直接红——这比下一个人去翻日志便宜。

.PARAMETER Readme
    README 路径，默认仓库根的 README.md。

.PARAMETER CiFile
    工作流路径，默认 .github/workflows/ci.yml。

.EXAMPLE
    pwsh tools/verify-matrix.ps1
#>
[CmdletBinding()]
param(
    [string] $Readme = $(Join-Path $PSScriptRoot '..' 'README.md'),
    [string] $CiFile = $(Join-Path $PSScriptRoot '..' '.github' 'workflows' 'ci.yml')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Readme)) { throw "找不到 README：$Readme" }
if (-not (Test-Path $CiFile)) { throw "找不到工作流：$CiFile" }

$readmeText = Get-Content $Readme -Raw -Encoding UTF8
$ciText = Get-Content $CiFile -Raw -Encoding UTF8

# 1. README 里的引用：`ci:job-name`
$references = [regex]::Matches($readmeText, '`ci:([a-z0-9][a-z0-9-]*)`') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

if ($references.Count -eq 0) {
    throw 'README 里没有任何 ci:<job> 引用——平台矩阵的可追溯性无从校验。'
}

# 2. ci.yml 里的 job 名：jobs: 之后、2 空格缩进的键
$jobsIndex = $ciText.IndexOf("`njobs:")
if ($jobsIndex -lt 0) { throw 'ci.yml 里找不到 jobs: 段。' }

$jobsSection = $ciText.Substring($jobsIndex)
$jobs = [regex]::Matches($jobsSection, '(?m)^  ([a-z][a-z0-9-]*):\s*$') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

if ($jobs.Count -eq 0) { throw 'ci.yml 的 jobs: 段里没有解析出任何 job。' }

# 3. 正向校验：每个引用都必须存在
$missing = @($references | Where-Object { $_ -notin $jobs })
if ($missing.Count -gt 0) {
    throw ("README 引用了不存在的 CI job：{0}。`n现有 job：{1}" -f
        ($missing -join '、'), ($jobs -join '、'))
}

# 4. 反向提示：验证类 job 没被任何一行引用，说明矩阵可能漏了某个平台
$verificationJobs = @($jobs | Where-Object { $_ -match '^(compile|smoke)-' })
$undocumented = @($verificationJobs | Where-Object { $_ -notin $references })
if ($undocumented.Count -gt 0) {
    Write-Warning ("这些验证 job 没有出现在平台矩阵里：{0}。`n" +
        '如果不是漏了，就把它们加进某一行（或从 CI 里删掉）——否则"矩阵"与"实际覆盖"会再次分叉。' -f
        ($undocumented -join '、'))
}

Write-Output ("OK：README 的 {0} 个 ci: 引用都能在 ci.yml 里找到（共 {1} 个 job）。" -f
    $references.Count, $jobs.Count)
