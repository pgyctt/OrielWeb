#Requires -Version 5.1
<#
.SYNOPSIS
    发布本仓库的示例应用（OrielDemo，Native AOT 单文件）。

.DESCRIPTION
    产物默认落到仓库根的 publish/<rid>/ 下。**按 RID 分子目录**不是偏好而是必需：
    三个平台/架构的 AOT 产物都是自包含的，混在一个目录里会互相覆盖，也没法判断
    "这份产物是给谁的"。分子目录之后，多次发布互不干扰，清理也只影响对应的那一份。

    RID 缺省时按当前平台与架构推断——在本机发布通常不需要给任何参数：

        pwsh tools/publish.ps1                  # 发布当前平台到 publish/<当前rid>/
        pwsh tools/publish.ps1 -Runtime linux-x64
        pwsh tools/publish.ps1 -Zip             # 额外打一个 zip，便于分发

.PARAMETER Runtime
    目标 RID，如 win-x64 / win-arm64 / linux-x64 / linux-arm64 / osx-arm64 / osx-x64。
    缺省按当前平台推断。

    只能指定**当前操作系统**的 RID：NativeAOT 不支持跨操作系统编译，
    指定别的平台会在这里被挡下并说明原因（跨架构则取决于是否装了目标架构的工具链）。

.PARAMETER Output
    产物根目录（相对路径按仓库根解析）。缺省 publish。真正的产物在 <Output>/<Runtime>/。

    只有缺省的 publish/ 在 .gitignore 里：换成别的目录（尤其是仓库内的目录）会直接出现在
    git status 中，用它当临时输出目录时记得自己收尾。

.PARAMETER Project
    要发布的示例项目，缺省 samples/OrielDemo。

.PARAMETER Configuration
    Release（缺省）或 Debug。注意 Debug 下 AOT 仍会启用，只是不优化、体积更大。

.PARAMETER Zip
    额外把产物目录压成 <Output>/<Runtime>.zip，便于分发。

.PARAMETER NoClean
    跳过的产物目录清理。默认会先删掉 <Output>/<Runtime>/——AOT 产物是一堆文件，
    留着上一次的会让"这次发布到底产出了什么"没法确认。

.EXAMPLE
    pwsh tools/publish.ps1
    在本机发布，产物在 publish/win-x64/。

.EXAMPLE
    pwsh tools/publish.ps1 -Runtime win-arm64 -Zip
    在本机（Windows x64）为 arm64 发布并打包——跨架构发布是允许的，但需要目标架构的工具链；
    这是同一台机器上为另一种架构出货的用法。

.NOTES
    跨操作系统发布不被支持（NativeAOT 的限制），所以三平台的产物要在各自的机器上发布——
    CI 也是这么做的：每个平台一个 runner，各自跑一次 dotnet publish。
#>
[CmdletBinding()]
param(
    # RID 的形状校验不只是友好提示：它会成为输出目录的子目录名，
    # 放任斜杠与 .. 进来就等于让调用方决定删除哪一个目录。
    [ValidatePattern('^[A-Za-z0-9]+-[A-Za-z0-9]+$')]
    [string]$Runtime,

    [string]$Output = 'publish',

    [string]$Project = 'samples/OrielDemo',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Zip,

    [switch]$NoClean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 仓库根从脚本位置推，而不是当前目录：调用方在任意目录下执行都应当得到同样的结果。
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Get-HostPlatform {
    if ($env:OS -eq 'Windows_NT') {
        return 'win'
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)) {
        return 'osx'
    }

    return 'linux'
}

if (-not $Runtime) {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $Runtime = "$(Get-HostPlatform)-$arch"
}

# NativeAOT 不支持**跨操作系统**编译：ILCompiler 会以
# "Cross-OS native compilation is not supported" 失败。提前挡住并说清原因，
# 比让调用方去读链接器输出的英文报错省事——这也正是 CI 里每个平台各有一个 runner 的原因。
#
# 跨**架构**（如 Linux 上发布 linux-arm64）是另一回事：能否成功取决于装了目标架构的工具链，
# 这里不拦，交给 ILCompiler 自己判断。
$targetPlatform = $Runtime.Split('-')[0].ToLowerInvariant()
$hostPlatform = Get-HostPlatform
if ($targetPlatform -ne $hostPlatform) {
    throw "无法为 $Runtime 发布：NativeAOT 不支持跨操作系统编译（当前是 $hostPlatform）。" +
          "请在目标平台上执行本脚本。"
}

$projectPath = Join-Path $RepoRoot $Project
if (-not (Test-Path $projectPath)) {
    throw "找不到要发布的项目：$projectPath"
}

$outputRoot = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $RepoRoot $Output }
$outputRoot = [System.IO.Path]::GetFullPath($outputRoot)
$publishDir = Join-Path $outputRoot $Runtime

# 清理是递归删除，所以这里要挡住会牵连仓库的传参：产物目录等于仓库根，
# 或者**是仓库根的某个祖先目录**时（-Output ../.. 配上与祖先同名的 RID），
# 一次 Remove-Item 就会把仓库连同它的上级一起带走。
#
# 注意别把判据写成"只比较相等"：产物目录是 <Output>/<Runtime>，末段永远由 RID 决定，
# 而 RID 的校验规则保证它含连字符，所以它不可能恰好等于通常的仓库目录名——
# 相等判断看起来在守门，实际永远不会触发。祖先判断才是能真正挡住事故的那条。
#
# 输出到仓库之外（例如 D:\artifacts）是合理用法，不拦。
$sep = [System.IO.Path]::DirectorySeparatorChar
$rootFull = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd($sep)
$publishFull = [System.IO.Path]::GetFullPath($publishDir).TrimEnd($sep)
$publishIsAncestor = $rootFull.StartsWith($publishFull + $sep, [System.StringComparison]::OrdinalIgnoreCase)
if ($publishFull -eq $rootFull -or $publishIsAncestor) {
    throw "拒绝把仓库（或其上级目录）当作产物目录（清理时会把仓库一起删掉）：$publishDir"
}

Write-Host "发布 $Project" -ForegroundColor Cyan
Write-Host "  RID        : $Runtime"
Write-Host "  配置       : $Configuration"
Write-Host "  产物目录   : $publishDir"

if (-not $NoClean -and (Test-Path $publishDir)) {
    Write-Host "  清理       : 删除上一次的产物" -ForegroundColor DarkGray
    Remove-Item -Path $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

# -o 把产物直接放到目标目录，而不是 SDK 默认的 bin/<cfg>/<tfm>/<rid>/publish/——
# 后者藏在多层构建路径下，"发布产物在哪"每次都要想一遍。
$publishArgs = @(
    'publish', $projectPath,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $publishDir,
    '--nologo'
)

Write-Host "  dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

# 断言主产物真的在：publish 退出码为 0 但产物缺失是可能的（例如中途被清理），
# 与其让使用者去目录里翻，不如在这里说清楚。
$exeName = if ($Runtime -like 'win-*') { 'OrielDemo.exe' } else { 'OrielDemo' }
$exePath = Join-Path $publishDir $exeName
if (-not (Test-Path $exePath)) {
    throw "发布完成但找不到主产物：$exePath"
}

$files = Get-ChildItem -Path $publishDir -File
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
$totalMb = [Math]::Round($totalBytes / 1MB, 2)

Write-Host ''
Write-Host "完成：$publishDir" -ForegroundColor Green
Write-Host "  主产物     : $exeName（$([Math]::Round((Get-Item $exePath).Length / 1MB, 2)) MB）"
Write-Host "  合计       : $($files.Count) 个文件，$totalMb MB"

if ($Zip) {
    # zip 放在 <Output>/ 下而不是产物目录内部，免得下次清理时把自己删了
    $zipPath = Join-Path $outputRoot "$Runtime.zip"
    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
    Write-Host "  压缩包     : $zipPath（$([Math]::Round((Get-Item $zipPath).Length / 1MB, 2)) MB）"
}
