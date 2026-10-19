<#
.SYNOPSIS
    Windows 后端的完整验证（运行冒烟 + Velopack 打包 + 机器断言自检）。

.DESCRIPTION
    三平台的验证脚本命名与职责**一一对应**：
      tools/verify-linux.sh    ← bash
      tools/verify-windows.ps1 ← 本文件（PowerShell）
      tools/verify-macos.sh    ← bash
    每个平台一个脚本、内部按阶段分节；CI 里各自只调用一行。

    为什么合成一个脚本（而不是拆成 -selftest / -pack 两个）：拆开后名字与另外两个平台
    对不上，而"哪个平台该跑什么"要跨文件去拼。阶段划分仍在脚本内部保留（下面用 Section
    标出），失败时逐阶段给结论；需要单独跑某一阶段时用 -Stages 选择。

    ============================ 三个阶段 ============================

    【冒烟】真实运行
      进程存活 8 秒 = 窗口创建成功且消息循环在跑。CI runner 无交互桌面，
      因此**不断言窗口可见性**，只断言进程未因启动异常立即退出。

    【打包】Velopack
      只做机器可判定的部分——打包成功、该有的产物都在、更新包与更新清单已按约定清除；
      "真装一次"要静默装到 runner 上，那一步在 ROADMAP 的人眼清单里（见 API.md 的打包一节）。

    【自检】机器断言（结论行判定）
      nav ipc theme capability multiwindow scheme threading tray tray-js
      exit / exit-timeout（子进程；**同时**判结论行与退出码）
      single-instance（双进程：第二个应立即成功退出并通知首实例）

    ============================ 判定口径 ============================
    自检以日志里的**结论行**为准（`XXX-SELFTEST: PASS`）：各平台的应用退出方式不同，
    退出码未必可靠地表达自检结果——macOS 上就出现过"结论 FAIL 却返回 0"。
    **例外是 exit / exit-timeout**：那两项的退出码本身就是被验证的契约（正常退出必须 0、
    看门狗强杀必须 ForceExitCode=2），因此同时判结论行与退出码。

    为什么用 Start-Process 而不是 `&` 调用，两个原因（都实测过）：
      1) pwsh 的 $LASTEXITCODE 在 `&` 调用之后仍可能是上一条原生命令的值、甚至未定义（$null），
         而 $null -ne 0 为真——第一次接进来时就是这样被误报成失败的；
      2) 自检输出必须落到日志里，失败时才看得到是哪一项断言挂了。GUI 子系统下 `&` 的
         重定向抓不到原生 stdout，Start-Process 的显式管道才行。

    失败原因会写成 GitHub 注解（`::error title=...::`）：注解用 API 就能读到
    （/check-runs/<job>/annotations），不必登录去翻日志——2026-10-02 排查失败时，
    日志要 token（403）而注解不要，这个差别直接决定了问题能不能在本地查下去。

.PARAMETER Stages
    要跑的阶段，逗号分隔：smoke,pack,selftest；默认 all（三个阶段全跑）。

.PARAMETER Demo
    自检要跑的可执行产物。默认 dist/demo/OrielDemo.exe（打包阶段的产物）。

.PARAMETER SmokeExe
    冒烟阶段要跑的可执行产物。默认 bin/Release/net10.0/win-x64/OrielDemo.exe（IL 构建）。
    刻意与自检分开：冒烟要的是"能不能起来"，用 IL 构建更快；自检跑的是**要发布的 AOT 产物**。

.PARAMETER Out
    日志目录。默认 $env:RUNNER_TEMP（CI 上）/ $env:TEMP（本地）。

.PARAMETER Publish
    自检阶段产物不存在时先跑一次 AOT 发布到 dist/demo。

.PARAMETER NoInstallVpk
    打包阶段跳过安装 vpk（本机已装时用）。

.PARAMETER SkipSingleInstance
    跳过单实例双进程取证。只在本机已被别的 OrielDemo 占用、或想快速迭代其余模式时用——
    CI 上**不要**加，那一项正是"第二个实例不建窗、立即退出"这条契约的唯一机器断言。

.EXAMPLE
    pwsh tools/verify-windows.ps1

.EXAMPLE
    pwsh tools/verify-windows.ps1 -Stages selftest -Out C:\temp\selftest-out
#>
[CmdletBinding()]
param(
    [string]$Stages = 'all',
    [string]$Demo,
    [string]$SmokeExe,
    [string]$Out,
    [switch]$Publish,
    [switch]$NoInstallVpk,
    [switch]$SkipSingleInstance
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot

function Test-Stage([string]$Name) {
    if ($Stages -eq 'all') { return $true }
    return (",$Stages," -like "*,$Name,*")
}

foreach ($stage in $Stages.Split(',')) {
    if ($stage.Trim() -notin @('all', 'smoke', 'pack', 'selftest')) {
        throw "未知 -Stages 取值：$stage（可选 all / smoke / pack / selftest）"
    }
}

if (-not $Out) {
    $Out = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { Join-Path ([IO.Path]::GetTempPath()) 'oriel-win-verify-out' }
}
New-Item -ItemType Directory -Path $Out -Force | Out-Null

if (-not $Demo) { $Demo = Join-Path $RepoRoot 'dist/demo/OrielDemo.exe' }
if (-not $SmokeExe) { $SmokeExe = Join-Path $RepoRoot 'samples/OrielDemo/bin/Release/net10.0/win-x64/OrielDemo.exe' }
$Demo = [IO.Path]::GetFullPath($Demo)
$SmokeExe = [IO.Path]::GetFullPath($SmokeExe)

$Failures = @()
$Summary = @()

function Section([string]$Title) {
    Write-Host ''
    Write-Host "===== $Title ====="
}

# 注意：下面的日志一律用 Write-Host 而不是 Write-Output。
# PowerShell 会把函数里**所有未捕获的输出并入返回值**——函数里混用 Write-Output 与
# `return [pscustomobject]` 会让调用方拿到数组而非对象，症状是"在此对象上找不到属性"。
# Write-Host 走信息流、不进管道，返回值因此干净。

Section '环境'
Write-Host "阶段      : $Stages"
Write-Host "冒烟产物  : $SmokeExe"
Write-Host "自检产物  : $Demo"
Write-Host "日志目录  : $Out"

# ===========================================================================
# 阶段 1：运行冒烟
# ===========================================================================
if (Test-Stage 'smoke') {
    Section '冒烟：真实运行（进程存活 8 秒）'
    # 进程存活 8 秒 = 窗口创建成功且消息循环在跑（CI runner 无交互桌面，
    # 不断言窗口可见性，只断言进程未因启动异常立即退出）。
    if (-not (Test-Path $SmokeExe)) {
        throw "未找到 demo 可执行文件：$SmokeExe（先跑 dotnet build samples/OrielDemo -c Release -r win-x64）"
    }

    $proc = Start-Process -FilePath $SmokeExe -PassThru
    Start-Sleep -Seconds 8
    if ($proc.HasExited) {
        throw "demo 启动后立即退出，ExitCode=$($proc.ExitCode)"
    }

    Write-Host '  [通过] 进程存活，消息循环运行中'
    Stop-Process -Id $proc.Id -Force
}

# ===========================================================================
# 发布 AOT（打包与自检的共同前置）
# ===========================================================================
# **必须排在 smoke 之后**：`dotnet publish -o dist/demo` 会把 bin/Release/net10.0/win-x64/
# 重建成 OrielDemo.dll + deps/runtimeconfig——那个目录里的 IL 构建 `OrielDemo.exe`
# 在 publish 之后就不存在了，冒烟阶段便找不到产物。CI 上就是这么红过一次：
# 合并脚本时把 publish 提到冒烟之前，冒烟报"未找到 demo 可执行文件"。
if ((Test-Stage 'pack' -or Test-Stage 'selftest') -and
    (-not (Test-Path (Join-Path $RepoRoot 'dist/demo/OrielDemo.exe')) -or $Publish)) {
    Section '发布 AOT（win-x64，供打包与自检用）'
    Push-Location $RepoRoot
    try {
        & dotnet publish samples/OrielDemo -c Release -r win-x64 -p:DebugType=none -o dist/demo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path (Join-Path $RepoRoot 'dist/demo/OrielDemo.exe'))) {
        throw '发布完成但找不到主产物：dist/demo/OrielDemo.exe'
    }
}

# ===========================================================================
# 阶段 2：Velopack 打包与产物断言
# ===========================================================================
if (Test-Stage 'pack') {
    Section '打包：Velopack（vpk pack + 产物齐备 + 只留能装的东西）'

    $packDir = Join-Path $RepoRoot 'dist/demo'
    $outputDir = Join-Path $RepoRoot 'dist/releases'

    if (-not (Test-Path (Join-Path $packDir 'OrielDemo.exe'))) {
        throw "打包目录不存在或缺少主产物：$packDir（先跑 dotnet publish -o dist/demo）"
    }

    if (-not $NoInstallVpk) {
        Write-Host '  --- 安装 vpk（钉住版本）---'
        & dotnet tool install --global vpk --version '1.2.*'
        # dotnet 全局工具落在 <家目录>/.dotnet/tools，而"家目录"与 PATH 分隔符三平台各不相同：
        # 原先写的 "$env:USERPROFILE\.dotnet\tools" 在 macOS/Linux 上 $env:USERPROFILE 是空的、
        # PATH 用的也是 ':' 而不是 ';'——拼出来的条目根本不是 vpk 所在的位置。
        $env:PATH = @($env:PATH, (Join-Path $HOME '.dotnet/tools')) -join [IO.Path]::PathSeparator
    }

    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        throw 'vpk 不在 PATH 上'
    }

    Write-Host '  --- 读取版本号 ---'
    [xml]$props = Get-Content (Join-Path $RepoRoot 'Directory.Build.props')
    $version = @($props.Project.PropertyGroup.Version)[0].Trim()
    Write-Host "  版本      : $version"

    Write-Host '  --- vpk pack ---'
    vpk pack --packId OrielDemo --packVersion $version --packTitle 'Oriel Demo' `
        --packAuthors 'OrielWeb Contributors' --packDir $packDir --outputDir $outputDir `
        --mainExe OrielDemo.exe --icon (Join-Path $RepoRoot 'samples/OrielDemo/app.ico') --msi --skip-updates -x -y
    if ($LASTEXITCODE -ne 0) {
        throw "vpk pack 失败（退出码 $LASTEXITCODE）"
    }

    # 更新包（*.nupkg）与更新清单（releases.*.json / assets.*.json / RELEASES*）都不要：
    # 本仓库不发更新源，清单还会指向一个已被删掉的包
    Write-Host '  --- 清除更新包与更新清单 ---'
    Get-ChildItem $outputDir -File | Where-Object {
        $_.Extension -in '.nupkg', '.json' -or $_.Name -like 'RELEASES*'
    } | Remove-Item -Force

    Write-Host '  --- 断言产物 ---'
    foreach ($name in @('OrielDemo-win-Setup.exe', 'OrielDemo-win-Portable.zip', 'OrielDemo-win.msi')) {
        if (-not (Test-Path (Join-Path $outputDir $name))) {
            $Failures += "pack 缺少产物 $name"
            Write-Host "  [失败] 缺少产物 $name"
            continue
        }
        Write-Host "  [通过] $name"
    }

    # 清完之后目录里只该剩安装产物——这条断言同时把"更新包与清单已删"变成了可执行形式。
    # 这里**不能**再去读更新清单：上一步刚把它删掉。第一版就是这么红的
    # （删清单之后仍断言 releases.win.json 存在），所以留这段注释。
    $leftover = Get-ChildItem $outputDir -File | Where-Object { $_.Name -notlike 'OrielDemo-win*' }
    if ($leftover) {
        $Failures += "pack 的 release 目录里还有不该有的文件：$($leftover.Name -join ', ')"
        Write-Host "  [失败] release 目录里还有不该有的文件：$($leftover.Name -join ', ')"
    }
    else {
        Write-Host "  [通过] 只留安装产物（$((Get-ChildItem $outputDir -File).Name -join ', ')）"
    }
}

# ===========================================================================
# 阶段 3：机器断言自检
# ===========================================================================
if (Test-Stage 'selftest') {

    # 产物已由上面那段"发布 AOT"保证存在（它排在 smoke 之后——顺序理由见那里的注释）
    if (-not (Test-Path $Demo)) {
        throw "找不到自检产物：$Demo（用 -Publish 先发布一次，或用 -Demo 指定）"
    }

    # 通用模式：只看结论行
    function Invoke-SelftestMode {
        param(
            [Parameter(Mandatory)] [string]$Mode,
            # 结论行正则。多个模式共用一个正则时用分组（如 nav/ipc/... 的公共形态）。
            [Parameter(Mandatory)] [string]$Pattern
        )

        $outFile = Join-Path $Out "selftest-$Mode.out.txt"
        $errFile = Join-Path $Out "selftest-$Mode.err.txt"

        $proc = Start-Process -FilePath $Demo -ArgumentList @('--selftest', $Mode) -Wait -PassThru `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile

        Write-Host "----- selftest $Mode（退出码 $($proc.ExitCode)）-----"
        if (Test-Path $outFile) { Get-Content $outFile -Encoding UTF8 | Write-Host }
        if (Test-Path $errFile) { Get-Content $errFile -Encoding UTF8 | Write-Host }

        $verdict = ''
        if (Test-Path $outFile) {
            $last = Select-String -Path $outFile -Pattern $Pattern | Select-Object -Last 1
            if ($last) { $verdict = $last.Line }
        }

        return [pscustomobject]@{
            Mode     = $Mode
            ExitCode = $proc.ExitCode
            Verdict  = $verdict
            OutFile  = $outFile
        }
    }

    Section '自检：机器断言（结论行判定）'
    $commonPattern = '^(NAV|IPC|THEME|CAPABILITY|MULTIWINDOW|SCHEME|THREADING|TRAY|TRAY-JS|GEOMETRY|WINDOWEVENTS|EVENT|CLOSE|WINDOW|WINDOWNEW|API)-SELFTEST: '
    foreach ($mode in @('nav', 'ipc', 'theme', 'capability', 'multiwindow', 'scheme', 'threading', 'tray', 'tray-js', 'geometry', 'geometry-frameless', 'windowevents', 'event', 'close', 'window', 'windownew', 'api')) {
        $result = Invoke-SelftestMode -Mode $mode -Pattern $commonPattern
        $Summary += "[$mode] $(if ($result.Verdict) { $result.Verdict } else { "无结论行（退出码 $($result.ExitCode)）" })"

        if ($result.Verdict -notmatch 'PASS') {
            $Failures += "selftest $mode 未通过"
            # 失败模式的日志尾巴也进注解：注解不需要 token 就能读，日志需要
            $tail = (Get-Content $result.OutFile -Encoding UTF8 -ErrorAction SilentlyContinue |
                     Where-Object { $_.Trim() } | Select-Object -Last 2) -join ' / '
            if ($tail) { $Summary += " << $tail" }
        }
    }

    Section '自检：退出语义（结论行 + 退出码）'
    # 这两项必须子进程取证：看门狗超时走 Environment.Exit，会把测试运行器一起带走。
    #  · exit          —— 正常退出，退出码必须是 0（看门狗不该在正常路径上强杀）。
    #  · exit-timeout  —— 看门狗超时强杀，退出码必须是 ForceExitCode（默认 2）。
    # 只看结论行会漏掉"看门狗没真的强杀"（进程正常退出了）；
    # 只看退出码会漏掉"事件没触发"。两者缺一都不算通过。
    $exitCases = @(
        [pscustomobject]@{ Mode = 'exit';         Pattern = '^EXIT-SELFTEST: ';         Expected = 0 }
        [pscustomobject]@{ Mode = 'exit-timeout'; Pattern = '^EXIT-TIMEOUT-SELFTEST: '; Expected = 2 }
    )

    foreach ($case in $exitCases) {
        $result = Invoke-SelftestMode -Mode $case.Mode -Pattern $case.Pattern
        $Summary += "[$($case.Mode)] $(if ($result.Verdict) { $result.Verdict } else { "无结论行（退出码 $($result.ExitCode)）" })"

        if ($result.Verdict -notmatch 'PASS') {
            $shown = if ($result.Verdict) { $result.Verdict } else { '无' }
            $Failures += "selftest $($case.Mode) 结论行非 PASS：$shown"
        }
        elseif ($result.ExitCode -ne $case.Expected) {
            $Failures += "selftest $($case.Mode) 退出码应为 $($case.Expected)，实际 $($result.ExitCode)"
        }
    }

    Section '自检：单实例（双进程）'
    # 这一项没法单进程自证：起两个进程，第二个应立即成功退出并通知首实例，
    # 第一个应收到激活请求（首实例自带 15 秒看门狗，不会挂住）。
    if ($SkipSingleInstance) {
        Write-Host '  （按 -SkipSingleInstance 跳过）'
    }
    else {
        # 先清掉可能残留的实例：否则"第二个实例"的判定会被上一个残留进程搅乱
        Stop-Process -Name OrielDemo -Force -ErrorAction SilentlyContinue

        $primary = Start-Process -FilePath $Demo -ArgumentList @('--selftest', 'single-instance') -PassThru `
            -RedirectStandardOutput (Join-Path $Out 'si-primary.log') `
            -RedirectStandardError (Join-Path $Out 'si-primary.err')

        Start-Sleep -Seconds 5
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $secondary = Start-Process -FilePath $Demo -ArgumentList @('--selftest', 'single-instance') -Wait -PassThru `
            -RedirectStandardOutput (Join-Path $Out 'si-secondary.log') `
            -RedirectStandardError (Join-Path $Out 'si-secondary.err')
        $sw.Stop()

        $primary.WaitForExit(20000) | Out-Null
        Write-Host "  [single-instance] 第二实例 退出码=$($secondary.ExitCode)（$([int]$sw.Elapsed.TotalSeconds)s） 首实例 退出码=$($primary.ExitCode)"

        foreach ($name in @('si-primary', 'si-secondary')) {
            $log = Join-Path $Out "$name.log"
            if (Test-Path $log) { Get-Content $log -Encoding UTF8 | ForEach-Object { Write-Host "    $_" } }
        }

        $siError = ''
        if ($secondary.ExitCode -ne 0) {
            $siError = "第二个实例退出码非 0：$($secondary.ExitCode)"
        }
        elseif ($sw.Elapsed.TotalSeconds -ge 10) {
            $siError = "第二个实例没有立即退出（$([int]$sw.Elapsed.TotalSeconds)s）"
        }
        else {
            $secondaryLog = Get-Content (Join-Path $Out 'si-secondary.log') -Encoding UTF8 -Raw
            if ($secondaryLog -notmatch 'SINGLE-INSTANCE-SECONDARY') {
                $siError = '第二个实例没有通知首实例'
            }
            else {
                $primaryLog = Get-Content (Join-Path $Out 'si-primary.log') -Encoding UTF8 -Raw
                if ($primaryLog -notmatch 'SINGLE-INSTANCE: PASS') {
                    $siError = '首实例没有收到激活请求'
                }
            }
        }

        if ($siError) {
            $Failures += "single-instance：$siError"
            Write-Host "  [失败] $siError"
        }
        else {
            Write-Host '  [通过] 第二个实例立即退出并通知了首实例'
        }
    }
}

# ===========================================================================
Section '结论'
# ===========================================================================

if ($Failures.Count -gt 0) {
    $detail = $Failures -join ' | '
    Write-Host "::error title=Windows 验证::$detail"
}
if ($Summary.Count -gt 0) {
    Write-Host "自检结论：$($Summary -join ' | ')"
}

if ($Failures.Count -eq 0) {
    Write-Host "全部通过（阶段：$Stages）"
    Write-Host "日志目录: $Out"
    Write-Host ''
    Write-Host '仍需人眼确认（本脚本无法机器判定）：窗口渲染是否正常、托盘图标是否真的出现在通知区。'
    exit 0
}

Write-Host "有 $($Failures.Count) 项未通过："
$Failures | ForEach-Object { Write-Host "  - $_" }
Write-Host "日志目录: $Out"
exit 1
