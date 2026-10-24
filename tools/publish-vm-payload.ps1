<#
.SYNOPSIS
    生成用于 VM 部署验证的自包含载荷（`.drill/vm/<rid>/`）。

.DESCRIPTION
    这批载荷的用途只有一个：推给**没有装 SDK 的干净机器**再执行，验证"目标机上不装 .NET 也能跑"。
    这与 CI 的 smoke job 不重复——那边跑在托管 runner 上，有 SDK、有 NuGet 缓存。

    两个参数是刻意的，不能省：

      --self-contained true   把运行时一起带上；否则目标机需要预装 .NET。
      -p:PublishAot=false     覆盖 csproj 里的 PublishAot=true。

    第二条尤其容易漏：漏了会得到 AOT 产物，而 AOT **不能从宿主机交叉发布到别的平台**
    （需要目标平台的链接器）。症状是 link 阶段报 `unrecognised emulation mode`，
    看起来像工具链坏了，实际是参数给错了。脚本发布完会检查 JIT 运行时的存在性，
    把这条留在断言里而不是留在注释里。

.PARAMETER Rid
    目标 RID。默认 win-x64。

.PARAMETER Out
    输出目录。默认 `.drill/vm/<rid>`，与 docs/CROSS-PLATFORM-VERIFICATION.md §5 的命名一致。

.EXAMPLE
    pwsh tools/publish-vm-payload.ps1 -Rid linux-x64
    pwsh tools/publish-vm-payload.ps1 -Rid win-x64
#>
[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64', 'win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',

    [string]$Out
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Out) {
    $Out = Join-Path $repoRoot ".drill/vm/$Rid"
}

$project = Join-Path $repoRoot 'samples/OrielDemo/OrielDemo.csproj'
if (-not (Test-Path $project)) {
    throw "找不到示例项目：$project"
}

Write-Host "载荷   : $Rid"
Write-Host "输出   : $Out"

dotnet publish $project `
    -c Release `
    -r $Rid `
    --self-contained true `
    -p:PublishAot=false `
    -o $Out `
    -v minimal

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）"
}

# ---- 断言：这必须是 JIT 运行时，不是 AOT 产物 ----
#
# AOT 产物里没有 coreclr（代码已编成原生），而"是不是 AOT"直接决定这份载荷能不能
# 从宿主机交叉发布、以及在目标机上是否还需要 .NET。查一个文件比读参数可靠：
# 参数可能被 csproj 里的条件覆盖，文件不会。
$jitRuntime = @('coreclr.dll', 'libcoreclr.so', 'libcoreclr.dylib') |
    Where-Object { Test-Path (Join-Path $Out $_) }

if (-not $jitRuntime) {
    throw "输出目录里没有 JIT 运行时（coreclr）：$Out 是 AOT 产物或发布未完成。" +
          "本脚本的用途是产出**自包含非 AOT** 载荷，见 docs/CROSS-PLATFORM-VERIFICATION.md §5。"
}

# 可执行文件名随平台变，断言它真的在。
$exe = if ($Rid -like 'win-*') { 'OrielDemo.exe' } else { 'OrielDemo' }
$exePath = Join-Path $Out $exe
if (-not (Test-Path $exePath)) {
    throw "输出目录里没有可执行文件：$exePath"
}

$size = [math]::Round(((Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
Write-Host "完成   : $exePath（$size MB，JIT 运行时 $($jitRuntime -join ', ')）"
