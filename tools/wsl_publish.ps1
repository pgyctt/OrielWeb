#Requires -Version 5.1
<#
.SYNOPSIS
    在 WSL 里发布 Linux 版示例应用（OrielDemo，Native AOT 单文件）。

.DESCRIPTION
    本脚本存在的原因只有一个：**publish.ps1 发不了 Linux 产物**。

    NativeAOT 不支持跨操作系统编译（ILCompiler 会以 "Cross-OS native compilation is
    not supported" 失败），所以 publish.ps1 会主动挡下 `-Runtime linux-x64`，让你去目标平台发。
    在 Windows 上"目标平台"就是 WSL——本脚本负责把发布丢进去，产物仍落在 **Windows 侧**的
    `publish/<rid>/`，与 publish.ps1 的产物按 RID 分目录共存、互不覆盖。

    它封装的就是这一条命令（在 WSL 的仓库目录里执行）：

        dotnet publish samples/OrielDemo -c Release -r linux-x64 -o /mnt/e/.../publish/linux-x64

    多做的三件事与 publish.ps1 一致：产物落到固定位置、发布前清掉旧产物（否则分不清这次
    产出了什么）、发布后校验主产物确实存在。

    注意 AOT 在 `/mnt/*`（跨文件系统）上明显更慢，首次发布可能要几分钟——这是设计使然，
    不是卡住了。脚本直接回显 dotnet 的进度，不必猜。

.PARAMETER Runtime
    Linux 的目标 RID，如 linux-x64 / linux-arm64。缺省 linux-x64。

    只接受 `linux-*`：本脚本的职责就是在 WSL 里发 Linux 产物，其他平台请用 publish.ps1
    （Windows）或 `oriel bundle`（macOS/Linux 的安装产物）。跨**架构**是允许的，但取决于
    WSL 里是否装了目标架构的工具链。

.PARAMETER Output
    产物根目录（相对路径按仓库根解析）。缺省 publish。真正的产物在 <Output>/<Runtime>/。

    只有缺省的 publish/ 在 .gitignore 里：换成别的目录（尤其是仓库内的目录）会直接出现在
    git status 中，用它当临时输出目录时记得自己收尾。

.PARAMETER Project
    要发布的示例项目，缺省 samples/OrielDemo。

.PARAMETER Configuration
    Release（缺省）或 Debug。注意 Debug 下 AOT 仍会启用，只是不优化、体积更大。

.PARAMETER Distro
    WSL 发行版名（如 Ubuntu）。缺省用 WSL 的默认发行版。

    显式指定是为了在有多个发行版、而默认那个没装 .NET SDK 时能指明用哪一个。

.PARAMETER Zip
    额外把产物目录压成 <Output>/<Runtime>.zip，便于分发。

.PARAMETER NoClean
    跳过产物目录清理。默认会先删掉 <Output>/<Runtime>/——AOT 产物是一堆文件，
    留着上一次的会让"这次发布到底产出了什么"没法确认。

.PARAMETER DryRun
    只打印将要执行的 WSL 命令与内联脚本，不真正发布。用来确认路径映射是否符合预期。

.EXAMPLE
    pwsh tools/wsl_publish.ps1
    在 WSL 里发布 linux-x64，产物在 publish/linux-x64/。

.EXAMPLE
    pwsh tools/wsl_publish.ps1 -Runtime linux-arm64 -Zip
    发布 arm64 并打包（需要 WSL 里装了目标架构的工具链）。

.EXAMPLE
    pwsh tools/wsl_publish.ps1 -Distro Ubuntu -DryRun
    只看看命令长什么样——路径映射不对时先跑这个。

.NOTES
    - 仓库路径从脚本位置推，再映射成 WSL 的 /mnt/<盘符>/... 形式。**放在 UNC 路径
      （\\server\share）或网络驱动器上的仓库不支持**——那种路径映射不到 /mnt 下，
      脚本会明确报错而不是发到一个空目录里。
    - 需要 WSL 里具备 .NET 10 SDK 与 AOT 工具链（clang、zlib1g-dev，另需 WebKitGTK/GTK3 开发包）。
      缺什么脚本会列出来，不会只丢一句英文报错。
    - 与 verify-linux.sh 的分工：那个脚本自己会发布（默认就发），产物在 SDK 默认路径
      bin/<cfg>/<tfm>/<rid>/publish/，供它随后的双后端取证使用。本脚本面向"出可分发的产物"，
      落点与 publish.ps1 对齐。
#>
[CmdletBinding()]
param(
    # 只放行 linux-* 是有意的：这个脚本的定位就是补 publish.ps1 的跨 OS 缺口。
    # 放任 win-*/osx-* 进来只会得到一句 ILCompiler 的英文报错。
    [ValidatePattern('^linux-[A-Za-z0-9]+$')]
    [string]$Runtime = 'linux-x64',

    [string]$Output = 'publish',

    [string]$Project = 'samples/OrielDemo',

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Distro,

    [switch]$Zip,

    [switch]$NoClean,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 仓库根从脚本位置推，而不是当前目录：调用方在任意目录下执行都应当得到同样的结果。
$RepoRoot = Split-Path -Parent $PSScriptRoot

<#
    Windows 路径 → WSL 路径。

    只处理 <盘符>:\... 这一种形态：WSL 把每个盘挂到 /mnt/<小写盘符>。
    UNC 路径（\\server\share\...）与相对路径在这里明确拒绝——前者映射不到 /mnt 下，
    后者在 cd 到仓库根之后就失去意义了，而"发到一个不存在的地方"比报错难查得多。
#>
function ConvertTo-WslPath {
    param([Parameter(Mandatory)][string]$WindowsPath)

    $full = [System.IO.Path]::GetFullPath($WindowsPath)

    if ($full.StartsWith('\\')) {
        throw "无法把 UNC 路径映射到 WSL：$full。请把仓库放在本地盘（如 E:\...）上，或在 WSL 里直接执行 dotnet publish。"
    }

    if ($full.Length -lt 3 -or $full[1] -ne ':') {
        throw "无法识别的 Windows 路径：$full"
    }

    $drive = [char]::ToLowerInvariant($full[0])
    $rest = $full.Substring(2).Replace('\', '/')
    return "/mnt/$drive$rest"
}

# WSL 可执行文件：Windows 10 1803+ 是 wsl.exe。找不到就直接说清楚，
# 而不是让后面的调用抛出一个看不懂的"找不到命令"。
if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    throw "找不到 wsl.exe。本脚本需要 WSL（wsl --install 安装，见 https://aka.ms/wsl）。"
}

$projectPath = Join-Path $RepoRoot $Project
if (-not (Test-Path $projectPath)) {
    throw "找不到要发布的项目：$projectPath"
}

$outputRoot = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $RepoRoot $Output }
$outputRoot = [System.IO.Path]::GetFullPath($outputRoot)
$publishDir = Join-Path $outputRoot $Runtime

# 清理是递归删除（下面会交给 WSL 里的 rm -rf），所以这里要挡住会牵连仓库的传参：
# 产物目录等于仓库根，或者**是仓库根的某个祖先目录**时（-Output ../.. 配上与祖先同名的 RID），
# 一次删除就会把仓库连同它的上级一起带走。
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

# 交给 WSL 的路径与主产物名。
$repoWsl = ConvertTo-WslPath $RepoRoot
$publishDirWsl = ConvertTo-WslPath $publishDir
$projectWsl = ($Project -replace '\\', '/')
$exeName = 'OrielDemo'
$cleanFlag = if ($NoClean) { '0' } else { '1' }

# 内联的 bash 脚本。
#
# 用单引号 here-string（不做 PowerShell 插值）再逐项替换占位符，最后整体 base64 送进 WSL：
# 直接拼引号传多行脚本必失败——PowerShell 会吃掉 $ 与 $()（反引号才是它的转义符），
# wsl.exe 还会再拼一次命令行。base64 绕开了这两层解析。
$bashTemplate = @'
set -euo pipefail

REPO="__REPO__"
PROJECT="__PROJECT__"
RID="__RID__"
CFG="__CFG__"
PUBLISH_DIR="__PUBLISH_DIR__"
EXE_NAME="__EXE_NAME__"
DO_CLEAN="__DO_CLEAN__"
WIN_REPO="__WIN_REPO__"

if [[ ! -d "$REPO" ]]; then
    echo "在 WSL 里找不到仓库目录：$REPO" >&2
    echo "（它由 Windows 路径 $WIN_REPO 映射而来；路径映射不对时先用 -DryRun 看命令）" >&2
    exit 2
fi

# .NET SDK 的发现顺序与 tools/verify-linux.sh 保持一致：DOTNET 环境变量 → PATH → 用户目录。
# 顺序不同会让"同一个 WSL 里两个脚本找到不同的 dotnet"，那是最难查的一类不一致。
DOTNET_BIN="${DOTNET:-}"
if [[ -z "$DOTNET_BIN" ]]; then
    if command -v dotnet >/dev/null 2>&1; then
        DOTNET_BIN="$(command -v dotnet)"
    elif [[ -x "$HOME/.dotnet/dotnet" ]]; then
        DOTNET_BIN="$HOME/.dotnet/dotnet"
    fi
fi

if [[ -z "$DOTNET_BIN" ]]; then
    echo "WSL 里找不到 .NET SDK。" >&2
    echo "安装到用户目录（无需 sudo）：" >&2
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0" >&2
    exit 2
fi

# AOT 的原生工具链前置。缺了就直接列出来——让 ILCompiler 抛英文报错再回头猜要装什么，
# 比在这里说清楚费事得多（这点与 verify-linux.sh 的环境检查同一套判据）。
missing=()
command -v clang >/dev/null 2>&1 || missing+=("clang")
if command -v dpkg >/dev/null 2>&1; then
    dpkg -s zlib1g-dev            >/dev/null 2>&1 || missing+=("zlib1g-dev")
    dpkg -s libwebkit2gtk-4.1-dev >/dev/null 2>&1 || missing+=("libwebkit2gtk-4.1-dev")
    dpkg -s libgtk-3-dev          >/dev/null 2>&1 || missing+=("libgtk-3-dev")
fi

if [[ ${#missing[@]} -gt 0 ]]; then
    echo "缺少 AOT 发布所需的依赖：" >&2
    printf '  - %s\n' "${missing[@]}" >&2
    echo >&2
    echo "请先执行（需要 sudo，只装一次）：" >&2
    echo "  sudo apt-get install -y ${missing[*]}" >&2
    exit 2
fi

echo "发行版      : $(. /etc/os-release 2>/dev/null && echo "${PRETTY_NAME:-未知}" || echo 未知)"
echo "dotnet      : $DOTNET_BIN（$("$DOTNET_BIN" --version)）"
echo "仓库（WSL） : $REPO"
echo "产物目录    : $PUBLISH_DIR"

if [[ "$DO_CLEAN" == "1" && -d "$PUBLISH_DIR" ]]; then
    echo "清理        : 删除上一次的产物"
    rm -rf "$PUBLISH_DIR"
fi
mkdir -p "$PUBLISH_DIR"

cd "$REPO"

echo
echo "dotnet publish $PROJECT -c $CFG -r $RID -o $PUBLISH_DIR"
echo "（/mnt 跨界文件系统 + AOT，首次可能要几分钟）"
echo
"$DOTNET_BIN" publish "$PROJECT" -c "$CFG" -r "$RID" -o "$PUBLISH_DIR" --nologo

# 断言主产物真的在：publish 退出码为 0 但产物缺失是可能的（例如中途被清理），
# 与其让使用者去目录里翻，不如在这里说清楚。
EXE="$PUBLISH_DIR/$EXE_NAME"
if [[ ! -f "$EXE" ]]; then
    echo "发布完成但找不到主产物：$EXE" >&2
    exit 2
fi

echo
echo "主产物      : $EXE_NAME"
'@

# 占位符 → 实际值。用有序表逐个替换而不是链式调用：一眼能看出"脚本里能出现哪些占位符"，
# 漏掉一个会在下面的断言里直接报出来，而不是把一个 __XXX__ 原样送进 bash。
$substitutions = [ordered]@{
    '__REPO__'        = $repoWsl
    '__PROJECT__'     = $projectWsl
    '__RID__'         = $Runtime
    '__CFG__'         = $Configuration
    '__PUBLISH_DIR__' = $publishDirWsl
    '__EXE_NAME__'    = $exeName
    '__DO_CLEAN__'    = $cleanFlag
    '__WIN_REPO__'    = $RepoRoot
}

$bashScript = $bashTemplate
foreach ($entry in $substitutions.GetEnumerator()) {
    $bashScript = $bashScript.Replace($entry.Key, $entry.Value)
}

# 占位符写错名字（例如 __RID_）不会报错，只会让脚本里留一段字面量。这里直接断言清掉。
$leftover = [regex]::Matches($bashScript, '__[A-Z_]+__')
if ($leftover.Count -gt 0) {
    throw "内联脚本里还有未替换的占位符：$((($leftover | ForEach-Object { $_.Value }) | Select-Object -Unique) -join ', ')"
}

# Windows 侧生成的是 CRLF，bash 对行尾的 \r 很敏感（set -u 下 "1\r" 不等于 "1"）。
$bashScript = $bashScript -replace "`r`n", "`n"

$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bashScript))

Write-Host "在 WSL 里发布 $Project" -ForegroundColor Cyan
Write-Host "  RID        : $Runtime"
Write-Host "  配置       : $Configuration"
if ($Distro) {
    Write-Host "  发行版     : $Distro"
}
Write-Host "  仓库（WSL）: $repoWsl"
Write-Host "  产物目录   : $publishDir"
if (-not $NoClean) {
    Write-Host "  清理       : 删除上一次的产物" -ForegroundColor DarkGray
}

if ($DryRun) {
    Write-Host ''
    Write-Host '--- 将执行的 WSL 命令 ---' -ForegroundColor DarkGray
    Write-Host "wsl$(if ($Distro) { " -d $Distro" }) -- bash -lc 'echo <base64> | base64 -d | bash'"
    Write-Host ''
    Write-Host '--- base64 解开后的内联脚本 ---' -ForegroundColor DarkGray
    Write-Host $bashScript
    return
}

# -l 是 login shell：让 dotnet 与 apt 装的工具在非交互调用下也有正常的 PATH。
$wslArgs = @()
if ($Distro) {
    $wslArgs += @('-d', $Distro)
}
$wslArgs += @('--', 'bash', '-lc', "echo $b64 | base64 -d | bash")

$previousEncoding = [Console]::OutputEncoding
try {
    # WSL 输出是 UTF-8；不设的话中文在 Windows PowerShell 的默认代码页下会变成乱码。
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    & wsl.exe @wslArgs
    $exitCode = $LASTEXITCODE
}
finally {
    [Console]::OutputEncoding = $previousEncoding
}

if ($exitCode -ne 0) {
    throw "WSL 里的发布失败（退出码 $exitCode）"
}

# 产物在 Windows 侧校验（publish/ 就在本机盘上），不必再进一次 WSL。
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
Write-Host "  目标系统   : 需 libwebkit2gtk-4.1 + GTK3 运行库（见 README「平台运行要求」）" -ForegroundColor DarkGray

if ($Zip) {
    # zip 放在 <Output>/ 下而不是产物目录内部，免得下次清理时把自己删了
    $zipPath = Join-Path $outputRoot "$Runtime.zip"
    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
    Write-Host "  压缩包     : $zipPath（$([Math]::Round((Get-Item $zipPath).Length / 1MB, 2)) MB）"
}

Pause