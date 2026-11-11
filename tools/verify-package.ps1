#Requires -Version 7
<#
.SYNOPSIS
    验证 OrielWeb 的包内 build logic（buildTransitive/OrielWeb.targets）。

.DESCRIPTION
    「包内 MSBuild build logic」不是新能力，而是消灭一个现有缺陷：
    消费方手写 <EmbeddedResource Include="wwwroot\**\*" /> 而不写 LogicalName 时，
    MSBuild 会把目录分隔符压成 '.'，库只能靠"最后一个 '.' 是扩展名"反推目录——
    文件名主干含 '.' 时（app.min.js）必然推错，而且是**静默**的：
    解压不报错，页面按原 URL 请求就是 404 白屏（见 API.md §4.3）。

    本脚本把整条「打包 → 消费 → 断言」的链路跑一遍：

      场景 A 零配置     samples/OrielMinimal（csproj 里一行 EmbeddedResource 都没有）
                        → 资源名必须是显式分隔符形式：OrielMinimal.wwwroot/app.min.js
      场景 B1 自己声明  同一个工程加回手写的 <EmbeddedResource Include="wwwroot\**\*" />
                        **带** LogicalName="OrielMinimal.wwwroot/%(RecursiveDir)%(Filename)%(Extension)"
                        → 受支持：targets 跳过自动内嵌，资源名与零配置逐字符一致，不重复嵌入
      场景 B2 旧写法    同一个工程加上述那一行但**不带** LogicalName
                        → 必须**构建失败**（ORIELWEB001）并给出正确写法。
                          旧写法已删除：资源名无法区分"目录"与"含点的文件名"，库猜不准，
                          而猜错的表现是运行期白屏——与其静默，不如在构建期就拦下。
      场景 C 显式关闭   同一个工程加 -p:OrielWebEmbeddedAssets=false
                        → 一条 wwwroot 资源都不该有

      另外断言消费方程序集带上了 targets 顺带注入的 AssemblyMetadata("OrielBuildConfiguration")——
      那是能力模型判定 Debug / Release 的第一条路径，而它同样只存在于包消费链上
      （build/ 与 buildTransitive/ 不随 ProjectReference 分发）。

.PARAMETER NoPack
    跳过 dotnet pack，直接用 dist/nuget 里现成的包（迭代调试用）。

.PARAMETER Aot
    额外跑一遍「NuGet 包消费 + 原生 AOT 发布」直测（场景 E）。

    默认关闭：它要跑一次完整的 AOT 发布（拉 NativeAOT 运行时包 + 链接），
    比前四个场景加起来还慢；而它验证的风险（裁剪后嵌入资源还在不在）
    是**平台无关**的 IL/裁剪行为，在一个平台上直测就够。
    CI 里只在 Windows 的 test job 上开它（那台的 MSVC 工具链是自带的）。

.PARAMETER KeepTemp
    保留临时目录，便于查看中间产物。

.EXAMPLE
    pwsh tools/verify-package.ps1
.EXAMPLE
    pwsh tools/verify-package.ps1 -Aot
#>
[CmdletBinding()]
param(
    [switch] $NoPack,
    [switch] $Aot,
    [switch] $KeepTemp
)

$ErrorActionPreference = 'Stop'

# dotnet 的构建输出是 UTF-8，而 Windows 控制台默认按 OEM 代码页解码，
# 不显式设置的话中文日志会变成乱码——失败时最需要读它，恰恰读不了。
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $repoRoot 'dist/nuget'
$sample = Join-Path $repoRoot 'samples/OrielMinimal'
# 临时工程刻意放在**仓库内**的 obj/ 下（.gitignore 已覆盖），而不是 %TEMP%：
# 它靠仓库根的 Directory.Build.props 提供 $(Version) 才能拼出正确的包版本号，
# 放到 %TEMP% 会让那个属性为空、还原直接失败。
$temp = Join-Path $repoRoot ('obj/verify-package/' + [guid]::NewGuid().ToString('N').Substring(0, 8))

$failures = [System.Collections.Generic.List[string]]::new()

function Test-Assert {
    param([bool] $Condition, [string] $Message)

    if ($Condition) {
        Write-Host "  [PASS] $Message" -ForegroundColor Green
    }
    else {
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        $script:failures.Add($Message)
    }
}

function Show-Resources {
    param([string[]] $Names)
    foreach ($name in $Names) {
        Write-Host "         $name" -ForegroundColor DarkGray
    }
}

# 资源名里的目录分隔符曾经在 Windows 上带 '\'（%(RecursiveDir) 的产物），于是断言只能归一后再比。
# 2026-10-11 起 targets 自己把 %(RecursiveDir) 归一成 '/'（README 承诺的就是这个形状），
# 所以除了这条"跨平台比较用"的归一，**另有一条硬断言**：原始资源名里不许出现反斜杠
# （见场景 A 的 `-not ($a.RawResources -like '*\*')`）。归一留着是为了让比较逻辑与平台无关，
# 但它再也不能掩盖 targets 那边漏了归一。
function ConvertTo-NormalizedNames {
    param([string[]] $Names)
    return @($Names | ForEach-Object { $_.Replace('\', '/') })
}

# 期望构建**失败**的版本：返回构建日志供断言（成功反而说明该报的错没报）。
function Invoke-SampleBuildExpectFailure {
    param(
        [string] $ProjectDir,
        [string] $Label,
        [string[]] $ExtraArguments = @()
    )

    $buildLog = (& dotnet build $ProjectDir -c Release -v:n @ExtraArguments 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0) {
        throw "$Label：期望构建失败，但它成功了——该拦的写法没被拦下。"
    }

    return $buildLog
}

function Invoke-SampleBuild {
    param(
        [string] $ProjectDir,
        [string] $Label,
        [string[]] $ExtraArguments = @()
    )

    $buildLog = (& dotnet build $ProjectDir -c Release -v:n @ExtraArguments 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        Write-Host $buildLog
        throw "$Label：构建失败（退出码 $LASTEXITCODE）。"
    }

    $dll = Join-Path $ProjectDir 'bin/Release/net10.0/OrielMinimal.dll'
    if (-not (Test-Path $dll)) {
        throw "$Label：没有产出 $dll。"
    }

    # 样例的默认行为是开窗（WinExe）。CI 上没有图形会话，直接跑会挂住等窗口关闭，
    # 所以走它的无头通道：只打印内嵌资源名，不初始化 UI。
    $output = @(& dotnet $dll --list-resources)
    if ($LASTEXITCODE -ne 0) {
        throw "$Label：运行产物失败（退出码 $LASTEXITCODE）。"
    }

    $rawResources = @($output | Where-Object { $_ -match '\S' })
    return [pscustomobject]@{
        Resources    = @(ConvertTo-NormalizedNames -Names $rawResources)
        RawResources = $rawResources
        BuildLog     = $buildLog
    }
}

# 复制样例并清掉 obj/bin：必须让 MSBuild 从零求值一次，
# 否则增量构建会直接复用上一次的资源项，"改了个属性但没重建"会被误判成通过。
function New-SampleCopy {
    param([string] $TargetDir)

    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
    Copy-Item (Join-Path $sample '*') $TargetDir -Recurse -Force
    Remove-Item (Join-Path $TargetDir 'obj') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $TargetDir 'bin') -Recurse -Force -ErrorAction SilentlyContinue

    # 覆盖成**绝对** feed 路径：样例里那份 nuget.config 写的是相对路径
    # （..\..\dist\nuget，相对配置自身），换个目录深度就指错地方了。
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="orielweb-local" value="$feed" />
  </packageSources>
</configuration>
"@
    Set-Content -Path (Join-Path $TargetDir 'nuget.config') -Value $nugetConfig -Encoding utf8
}

Write-Host ''
Write-Host '=== OrielWeb 包内 build logic 验证 ===' -ForegroundColor Cyan

try {
    # 只清 OrielWeb **自己那一个包**的缓存，不把整个 NUGET_PACKAGES 指到临时目录。
    #
    # 为什么要清：同一个版本号重复 pack 时，NuGet 会拿旧的解压结果，
    # 于是"改了 targets 却没生效"会伪装成通过——这是本脚本存在的意义所在。
    #
    # 为什么不再整个指过去：那样每个场景都要从零还原全部依赖（含 NativeAOT 运行时包，
    # 数百 MB × 4 个场景）。本地实测一轮 20 分钟以上，而 CI 的 test job 超时是 20 分钟——
    # 这条验证会把整个 job 顶到红线上。需要"干净"的只有本库自己：其余依赖用共享缓存
    # 不改变任何一条断言的结果（断言看的是资源名与构建日志，与依赖从哪来无关）。
    #
    # **全局包目录不能猜**：它可能被 NuGet.Config 的 globalPackagesFolder 或 NUGET_PACKAGES
    # 改到任意位置（本机就是 D:\NuGet\packages）。猜错的后果不是报错，而是"清了个不存在的
    # 目录"——旧包照旧命中，验证在旧产物上假通过。所以问 dotnet 自己。
    $globalPackages = $null
    $localsLine = (& dotnet nuget locals global-packages --list 2>&1 | Select-Object -First 1)
    if ($localsLine -match 'global-packages:\s*(.+)$') {
        $globalPackages = $Matches[1].Trim()
    }

    if ([string]::IsNullOrEmpty($globalPackages)) {
        # 解析不到就退回默认位置，并明说——静默回退会让"没清到"看起来像"清干净了"。
        $globalPackages = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
        Write-Host "未能解析 dotnet 的全局包目录，按默认位置处理：$globalPackages" -ForegroundColor Yellow
    }

    $orielCache = Join-Path $globalPackages 'orielweb'
    if (Test-Path $orielCache) {
        Write-Host "清理本库的包缓存：$orielCache" -ForegroundColor DarkGray
        Remove-Item $orielCache -Recurse -Force

        # 删不掉就红，不要带着旧包继续跑——那会让整套断言失去意义。
        if (Test-Path $orielCache) {
            throw "无法清理本库的包缓存：$orielCache。请手动删除后重试（否则验证会拿旧包假通过）。"
        }
    }

    New-Item -ItemType Directory -Force -Path $temp | Out-Null

    # ── 1. 打包 ─────────────────────────────────────────────────────
    if (-not $NoPack) {
        Write-Host ''
        Write-Host '--- 1. dotnet pack ---' -ForegroundColor Cyan
        & dotnet pack (Join-Path $repoRoot 'src/OrielWeb/OrielWeb.csproj') -c Release -o $feed | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet pack 失败。'
        }
    }

    $nupkg = Get-ChildItem $feed -Filter 'OrielWeb.*.nupkg' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $nupkg) {
        throw "在 $feed 里找不到 OrielWeb.*.nupkg。"
    }

    Write-Host ''
    Write-Host "--- 2. 包内容（$($nupkg.Name)）---" -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $zip.Dispose()
    }

    # 文件名必须是 <PackageId>.targets：NuGet 只自动导入这个名字。
    Test-Assert ($entries -contains 'buildTransitive/OrielWeb.targets') `
        'nupkg 里有 buildTransitive/OrielWeb.targets（NuGet 只自动导入这个名字）'
    Test-Assert ($entries -contains 'analyzers/dotnet/cs/OrielWeb.Generators.dll') `
        'nupkg 里仍有源生成器（没被这次改动挤掉）'

    # ── 场景 A：零配置 ──────────────────────────────────────────────
    Write-Host ''
    Write-Host '--- 3. 场景 A：零配置（csproj 里没有 EmbeddedResource）---' -ForegroundColor Cyan
    $a = Invoke-SampleBuild -ProjectDir $sample -Label '场景 A'
    Show-Resources -Names $a.Resources

    Test-Assert ($a.Resources -contains 'OrielMinimal.wwwroot/app.min.js') `
        '含点文件名 app.min.js 拿到显式分隔符资源名（不会再被反推成 app/min.js）'
    Test-Assert ($a.Resources -contains 'OrielMinimal.wwwroot/vendor.bundle.js') `
        '第二个含点文件名 vendor.bundle.js 同样正确'
    Test-Assert ($a.Resources -contains 'OrielMinimal.wwwroot/assets/img/logo.svg') `
        '嵌套目录 assets/img/logo.svg 保持层级'
    Test-Assert ($a.RawResources -contains 'OrielMinimal.wwwroot/assets/img/logo.svg') `
        '嵌套资源的**原始**资源名就是文档承诺的形状（正斜杠，不是 assets\img\logo.svg）'
    Test-Assert (-not ($a.RawResources | Where-Object { $_ -like '*\*' })) `
        '所有资源名都不含反斜杠：targets 把 %(RecursiveDir) 归一成 / 了（README 承诺的显式分隔符）'
    Test-Assert ($a.Resources -contains 'OrielMinimal.wwwroot/index.html') `
        '普通文件 index.html 正常嵌入'
    Test-Assert (-not ($a.Resources -contains 'OrielMinimal.wwwroot.app.min.js')) `
        '没有出现按 "." 反推路径的资源名（那种形式已不再支持）'
    Test-Assert ($a.BuildLog -match '已自动内嵌') `
        '构建日志里能看到"已自动内嵌"的说明'

    # 顺带注入的 AssemblyMetadata("OrielBuildConfiguration")：能力模型判定 Debug / Release 的
    # **第一条**路径（取不到才回退到 DebuggableAttribute）。它在仓库内用 ProjectReference 的场景
    # 拿不到（build/ 与 buildTransitive/ 只随包分发），所以只能在这条消费链上验证。
    #
    # 证据强度如实说明：这是"这两个字符串以相邻 SerString 的形式躺在程序集里"的存在性证据，
    # 不是"用反射读出来等于 Release"——但 key 名足够独特、targets 里只有这一处写入，
    # 而 attribute 的 blob 格式正是 [长度][key][长度][value]，按字节匹配不会误命中别处。
    $dllPath = Join-Path $sample 'bin/Release/net10.0/OrielMinimal.dll'
    $raw = [System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes($dllPath))
    Test-Assert ($raw -match 'OrielBuildConfiguration[\x00-\x20]Release') `
        '消费方程序集带上 OrielBuildConfiguration=Release 元数据（能力模型取 Debug/Release 的第一条路径）'

    # ── 场景 B1：自己声明，带 LogicalName（受支持）──────────────────
    Write-Host ''
    Write-Host '--- 4. 场景 B1：自己声明（带 LogicalName）---' -ForegroundColor Cyan
    $ownDir = Join-Path $temp 'own'
    New-SampleCopy -TargetDir $ownDir

    $ownCsproj = Join-Path $ownDir 'OrielMinimal.csproj'
    $ownItem = @'
  <ItemGroup>
    <!-- 自己声明也可以，但必须带 LogicalName（显式 '/' 分隔符：%(RecursiveDir) 在 Windows 上带反斜杠，
         所以与 README 的推荐写法一样把它归一成 '/'） -->
    <EmbeddedResource Include="wwwroot\**\*"
                      LogicalName="OrielMinimal.wwwroot/$([System.String]::Copy('%(RecursiveDir)').Replace('\','/'))%(Filename)%(Extension)" />
  </ItemGroup>

</Project>
'@
    $csprojText = Get-Content $ownCsproj -Raw -Encoding utf8
    Set-Content -Path $ownCsproj -NoNewline -Encoding utf8 -Value $csprojText.Replace('</Project>', $ownItem)

    $b1 = Invoke-SampleBuild -ProjectDir $ownDir -Label '场景 B1'
    Show-Resources -Names $b1.Resources

    Test-Assert ($b1.Resources -contains 'OrielMinimal.wwwroot/app.min.js') `
        '自己声明（带 LogicalName）时资源名与零配置逐字符一致'
    Test-Assert ($b1.RawResources -contains 'OrielMinimal.wwwroot/assets/img/logo.svg') `
        '自声明的嵌套资源名与零配置**原始形态**逐字符一致（不靠归一兜底）'
    Test-Assert ($b1.BuildLog -match '跳过自动内嵌') `
        '构建日志里能看到"跳过自动内嵌"（说明没有重复嵌入同一批文件）'

    # ── 场景 B2：旧写法（不带 LogicalName）——已删除，必须构建失败 ──
    Write-Host ''
    Write-Host '--- 5. 场景 B2：旧写法（不带 LogicalName）应当构建失败 ---' -ForegroundColor Cyan
    $legacyDir = Join-Path $temp 'legacy'
    New-SampleCopy -TargetDir $legacyDir

    $legacyCsproj = Join-Path $legacyDir 'OrielMinimal.csproj'
    $legacyItem = @'
  <ItemGroup>
    <!-- 旧写法：不写 LogicalName（2026-10-03 起不再支持） -->
    <EmbeddedResource Include="wwwroot\**\*" />
  </ItemGroup>

</Project>
'@
    $csprojText = Get-Content $legacyCsproj -Raw -Encoding utf8
    Set-Content -Path $legacyCsproj -NoNewline -Encoding utf8 -Value $csprojText.Replace('</Project>', $legacyItem)

    $legacyLog = Invoke-SampleBuildExpectFailure -ProjectDir $legacyDir -Label '场景 B2'

    Test-Assert ($legacyLog -match 'ORIELWEB001') `
        '旧写法在构建期就被拦下（错误码 ORIELWEB001），而不是留到运行期变成白屏'
    Test-Assert ($legacyLog -match 'LogicalName') `
        '错误信息里给出了正确写法（带 LogicalName 的自声明）'

    # ── 场景 C：显式关闭 ────────────────────────────────────────────
    Write-Host ''
    Write-Host '--- 6. 场景 C：OrielWebEmbeddedAssets=false ---' -ForegroundColor Cyan
    $offDir = Join-Path $temp 'off'
    New-SampleCopy -TargetDir $offDir

    $c = Invoke-SampleBuild -ProjectDir $offDir -Label '场景 C' `
        -ExtraArguments @('-p:OrielWebEmbeddedAssets=false')
    Show-Resources -Names $c.Resources

    Test-Assert (-not ($c.Resources | Where-Object { $_ -like 'OrielMinimal.wwwroot*' })) `
        '关掉之后一条 wwwroot 资源都不嵌入'

    # ── 场景 D：路径含空格与中文 ────────────────────────────────────
    #
    # 目标是"消费方的工程放在哪里有影响吗"。资源名由 targets 里的 LogicalName 决定
    # （固定前缀 OrielMinimal.wwwroot），与工程目录**无关**——所以这一格的期望就是
    # "与场景 A 逐字符一致"。真出事的地方在于路径里的空格与非 ASCII 字符穿过了
    # MSBuild 的 glob / %(RecursiveDir) / 临时文件路径，而那种错的表现往往是
    # "少嵌了某个文件"或"资源名里混进了目录片段"，都不是显式报错。
    #
    # 为什么放在仓库内的 obj/ 下而不是 %TEMP%：见 $temp 的说明（要靠仓库根的
    # Directory.Build.props 提供 $(Version)）。中文与空格加在这一层即可。
    Write-Host ''
    Write-Host '--- 7. 场景 D：工程路径含空格与中文 ---' -ForegroundColor Cyan
    $oddDir = Join-Path $temp '带 空格 的目录'
    New-SampleCopy -TargetDir $oddDir

    Write-Host "  工程路径：$oddDir" -ForegroundColor DarkGray

    $d = Invoke-SampleBuild -ProjectDir $oddDir -Label '场景 D'
    Show-Resources -Names $d.Resources

    # 最强的一条：与场景 A 的资源名集合**逐字符一致**（顺序无关）。
    $onlyInA = @(Compare-Object $a.Resources $d.Resources |
        Where-Object { $_.SideIndicator -eq '<=' } | ForEach-Object { $_.InputObject })
    $onlyInD = @(Compare-Object $a.Resources $d.Resources |
        Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object { $_.InputObject })

    Test-Assert (($onlyInA.Count -eq 0) -and ($onlyInD.Count -eq 0)) `
        '路径含空格与中文时，资源名与场景 A 逐字符一致'
    if ($onlyInA.Count -gt 0 -or $onlyInD.Count -gt 0) {
        Write-Host "         场景 A 独有：$($onlyInA -join '、')" -ForegroundColor DarkGray
        Write-Host "         场景 D 独有：$($onlyInD -join '、')" -ForegroundColor DarkGray
    }

    Test-Assert ($d.Resources -contains 'OrielMinimal.wwwroot/app.min.js') `
        '含点文件名在非 ASCII 路径下仍拿到显式分隔符资源名'
    Test-Assert (-not ($d.RawResources | Where-Object { $_ -like '*\*' })) `
        '非 ASCII 路径下资源名依然不含反斜杠'

    # ── 场景 E：NuGet 包消费 + 原生 AOT 发布（可选）──────────────────
    if ($Aot) {
        Write-Host ''
        Write-Host '--- 8. 场景 E：NuGet 包消费 + 原生 AOT 发布 ---' -ForegroundColor Cyan

        # AOT 产物是**原生可执行文件**，不能再用 `dotnet <dll>` 起（场景 A 那条路）。
        # 所以这里要真的跑一次 publish，并对原生产物断言。
        $aotRid = if ($IsWindows) { 'win-x64' }
        elseif ($IsMacOS) {
            if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' }
        }
        else { 'linux-x64' }

        $aotDir = Join-Path $temp 'aot'
        $aotLog = (& dotnet publish $sample -c Release -r $aotRid -o $aotDir 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0) {
            Write-Host $aotLog
            throw "场景 E：AOT 发布失败（退出码 $LASTEXITCODE，RID $aotRid）。"
        }

        $aotExe = Join-Path $aotDir $(if ($IsWindows) { 'OrielMinimal.exe' } else { 'OrielMinimal' })
        Test-Assert (Test-Path $aotExe) "AOT 产物存在（$aotRid）"

        if (Test-Path $aotExe) {
            # **这一格的核心**：裁剪后的原生产物里，嵌入资源必须与托管构建逐字符一致。
            # 少了哪一个，消费方的页面就在运行期 404（而不是构建期报错）——
            # 那正是这份 targets 存在的意义所在。
            #
            # **为什么经文件中转，而不是 `@(& $aotExe --list-resources)`**：
            # 样例是 `OutputType=WinExe`，其 PE Subsystem 是 2（GUI）而不是 3（Console）。
            # 那种进程没有自己的控制台，`Console.WriteLine` 的 stdout 在"变量捕获"这条
            # 路径上会丢——2026-11-11 实测：同一个 exe 直接跑能看到 4 行，`@(& …)` 得到 0 条，
            # 而 `| Set-Content` / `| Out-String` / `cmd /c` 都能拿到。托管构建那条
            # （`dotnet <dll>`）不受影响，因为 `dotnet` 自己是 Console 子系统的 apphost。
            # 结论：对 WinExe 的 AOT 产物不要依赖 stdout 捕获，落到文件再读回来最稳。
            $listFile = Join-Path $aotDir 'resources.txt'
            & $aotExe --list-resources | Set-Content -Path $listFile -Encoding utf8

            if ($LASTEXITCODE -ne 0) {
                throw "场景 E：AOT 产物运行失败（退出码 $LASTEXITCODE）。"
            }

            $aotResources = @(ConvertTo-NormalizedNames `
                    -Names @(Get-Content $listFile -Encoding utf8 | Where-Object { $_ -match '\S' }))
            Show-Resources -Names $aotResources

            # 防"两边都空"的假通过：托管构建那边必须有资源，否则这条断言毫无意义。
            Test-Assert ($a.Resources.Count -gt 0) '托管构建确实有嵌入资源（下面两条比较才有意义）'
            Test-Assert ($aotResources.Count -gt 0) `
                'AOT 产物能被问出嵌入资源（不是"两边都空"式的假通过）'

            $missingInAot = @($a.Resources | Where-Object { $_ -notin $aotResources })
            $extraInAot = @($aotResources | Where-Object { $_ -notin $a.Resources })

            Test-Assert ($missingInAot.Count -eq 0) `
                'AOT 产物里的嵌入资源一个都不少（裁剪没有把 wwwroot 带走）'
            Test-Assert ($extraInAot.Count -eq 0) `
                'AOT 产物里也没有多出资源（targets 不会在 publish 路径上重复嵌入）'

            if ($missingInAot.Count -gt 0) {
                Write-Host "         AOT 缺失：$($missingInAot -join '、')" -ForegroundColor DarkGray
            }
            if ($extraInAot.Count -gt 0) {
                Write-Host "         AOT 多出：$($extraInAot -join '、')" -ForegroundColor DarkGray
            }
        }
    }
}
finally {
    if (-not $KeepTemp) {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host ''
        Write-Host "临时目录保留在：$temp" -ForegroundColor Yellow
    }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "验证失败：$($failures.Count) 项不符合预期" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host '验证通过：包内 build logic 按约定工作；旧写法（不带 LogicalName）在构建期被拦下。' -ForegroundColor Green
exit 0
