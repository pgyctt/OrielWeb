#Requires -Version 7.0
<#
.SYNOPSIS
    校验 README 平台矩阵里的 `ci:<job>` 引用都指向真实存在的 CI job，
    且每个 job 的 runner 与它声称的平台/架构一致。

.DESCRIPTION
    平台矩阵里的每一行状态都是一个**承诺**。承诺与仓库证据脱节时，最坏情况不是"少测了一个平台"，
    而是"以为测过了"——2026-10-23 的评审就撞上过一次：README 写着 Linux arm64"xvfb 冒烟"，
    而 ci.yml 里只有一条 IL 交叉编译。那句话看起来像证据，实际没有任何东西能复现它。

    所以把"证据"变成一个可核对的名字：README 写 `ci:smoke-linux-arm64`，本脚本确认
    ci.yml 里真有这个 job。引用错了 CI 直接红——这比下一个人去翻日志便宜。

    2026-11-11 增强（三件事）：

    1. **job 的 runner 必须与 job 名里的平台/架构一致**。名字说 `-x64` 却跑在 arm runner 上，
       那个 job 跑的不是它声称的东西——而 0.4.3 的 NSRect ABI 崩溃正是"Intel 上没人跑"
       这类盲区的产物。这条能直接抓到"名字与 runs-on 错配"。

    2. **README 行声明的架构必须被该行引用的 job 真正覆盖**。引用存在不等于覆盖对：
       `Windows x64/arm64` 引用 `ci:compile-windows` 看着没问题，而那个 job 只 publish
       `win-x64`——arm64 一格是空的。这条就是为抓它写的（首次运行即命中）。

    3. **声称"真机验证"的行必须有 `ci:` 引用**。防止"为了让脚本通过而把引用删掉"，
       同时保证"验证过"这句话背后总有东西可查。

    runner 的架构映射表（`$RunnerRids`）是**手工维护**的：GitHub 变更托管镜像的架构时，
    必须同步复核那张表。写成显式表而不是"从名字猜"，是因为猜错的表现恰好是"以为测过了"。

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

# 0. 托管 runner → 它实际跑的 RID。
#
# **这张表是手工维护的**：GitHub 换镜像架构时必须复核（换了的症状是"矩阵说 arm64、
# 实际跑 x64"，而那正是本脚本要防的）。之所以不"从名字猜"，是因为 `macos-latest`
# 这种名字本身不含架构信息，猜错的后果与不校验一样。
#
# 不在表里的 runner 让脚本失败而不是跳过：新增 runner 时应当有人确认它的架构，
# 而不是让它悄悄绕过校验。
$RunnerRids = @{
    'windows-latest'   = 'win-x64'
    'windows-2022'     = 'win-x64'
    'ubuntu-latest'    = 'linux-x64'
    'ubuntu-24.04'     = 'linux-x64'
    'ubuntu-24.04-arm' = 'linux-arm64'
    'macos-latest'     = 'osx-arm64'
    'macos-14'         = 'osx-arm64'
    'macos-15'         = 'osx-arm64'
    'macos-15-intel'   = 'osx-x64'
    'macos-13'         = 'osx-x64'
}

# 1. README 里的引用：`ci:job-name`
$references = [regex]::Matches($readmeText, '`ci:([a-z0-9][a-z0-9-]*)`') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

if ($references.Count -eq 0) {
    throw 'README 里没有任何 ci:<job> 引用——平台矩阵的可追溯性无从校验。'
}

# 2. ci.yml 里的 job：名字 + runs-on + 它明确 publish/build 的 RID
$jobsIndex = $ciText.IndexOf("`njobs:")
if ($jobsIndex -lt 0) { throw 'ci.yml 里找不到 jobs: 段。' }

$jobsSection = $ciText.Substring($jobsIndex)
$jobMatches = [regex]::Matches($jobsSection, '(?m)^  ([a-z][a-z0-9-]*):\s*$')

if ($jobMatches.Count -eq 0) { throw 'ci.yml 的 jobs: 段里没有解析出任何 job。' }

$jobInfo = @{}
for ($k = 0; $k -lt $jobMatches.Count; $k++) {
    $name = $jobMatches[$k].Groups[1].Value
    $start = $jobMatches[$k].Index
    $end = if ($k + 1 -lt $jobMatches.Count) { $jobMatches[$k + 1].Index } else { $jobsSection.Length }
    $block = $jobsSection.Substring($start, $end - $start)

    $runsOnMatch = [regex]::Match($block, '(?m)^\s+runs-on:\s*([^\s#]+)')
    $runsOn = if ($runsOnMatch.Success) { $runsOnMatch.Groups[1].Value.Trim() } else { '' }

    # 明确写出的 RID（`-r win-x64` / `--runtime linux-arm64`）
    $explicitRids = @([regex]::Matches($block, '(?:-r|--runtime)\s+([a-z0-9]+-[a-z0-9]+)') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)

    $jobInfo[$name] = [pscustomobject]@{
        Name         = $name
        RunsOn       = $runsOn
        ExplicitRids = $explicitRids
        Block        = $block
    }
}

$jobs = @($jobInfo.Keys | Sort-Object)
if ($jobs.Count -eq 0) { throw 'ci.yml 的 jobs: 段里没有解析出任何 job。' }

# 3. 正向校验：每个引用都必须存在
$missing = @($references | Where-Object { -not $jobInfo.ContainsKey($_) })
if ($missing.Count -gt 0) {
    throw ("README 引用了不存在的 CI job：{0}。`n现有 job：{1}" -f
        ($missing -join '、'), ($jobs -join '、'))
}

# 4. job 名里的平台/架构 ↔ runs-on 的架构
#
# **只校验 `smoke-*`**：`compile-*` 是 IL 交叉编译（不 AOT、不跑产物），
# runner 平台与目标 RID 无关——`compile-macos` 刻意跑在 windows-latest 上，
# 因为"编译 osx 的 IL"在哪个 runner 上都一样，Windows 的最便宜。
# 对它们断言"名字说 macos、就得跑 macos runner"会误报一处刻意设计。
#
# 而 `smoke-*` 要真的把产物跑起来，名字里的平台必须是它跑的 runner——
# 名字说 `-x64` 却跑在 arm runner 上，那个 job 跑的不是它声称的东西。
#
# 只对**名字里显式带架构后缀**的 job 断言架构（`-arm64` / `-x64`），
# 不带后缀的名字（如 `smoke-macos`）由第 5 步的覆盖校验负责——
# 从名字推断它的架构等于重复 GitHub 的默认值，而那个默认值会变。
$namePlatformRules = @(
    @{ Pattern = 'windows'; Expect = '^windows-' }
    @{ Pattern = 'linux';   Expect = '^ubuntu-' }
    @{ Pattern = 'macos';   Expect = '^macos-' }
)

foreach ($job in ($jobs | Where-Object { $_ -match '^smoke-' })) {
    $info = $jobInfo[$job]

    if ([string]::IsNullOrWhiteSpace($info.RunsOn)) {
        throw "CI job「$job」没有 runs-on——无法校验它跑在哪个平台。"
    }

    # 4a. 平台
    foreach ($rule in $namePlatformRules) {
        if ($job -notmatch $rule.Pattern) { continue }

        if ($info.RunsOn -notmatch $rule.Expect) {
            throw ("CI job「{0}」的名字说的是 {1}，却跑在 {2} 上。" -f
                $job, $rule.Pattern, $info.RunsOn) +
                '`smoke-*` 要真的把产物跑起来，名字里的平台必须是它跑的 runner。'
        }
        break
    }

    # 4b. 显式架构后缀
    $wantArch = $null
    if ($job -match '-arm64$') { $wantArch = 'arm64' }
    elseif ($job -match '-x64$') { $wantArch = 'x64' }

    if ($null -ne $wantArch) {
        if (-not $RunnerRids.ContainsKey($info.RunsOn)) {
            # 注意 `-f` 与 `+` 的优先级：`("A{0}" + "B" -f $x)` 里 `-f` 只作用于
            # 第二个字符串，`{0}` 不会被替换（错误消息里留着字面的 {0}，等于丢了排查线索）。
            # 先把整段拼好再 `-f`。
            throw (("CI job「{0}」跑在未登记的 runner「{1}」上：请在 tools/verify-matrix.ps1 的 " +
                'RunnerRids 表里补上它的架构（新增 runner 时要有人确认，不能悄悄绕过校验）。') -f
                $job, $info.RunsOn)
        }

        $actualRid = $RunnerRids[$info.RunsOn]
        if ($actualRid -notmatch "-$wantArch$") {
            throw ("CI job「{0}」的名字带 -{1}，但它跑的 runner「{2}」实际是 {3}。" -f
                $job, $wantArch, $info.RunsOn, $actualRid)
        }
    }
}

# 5. README 行的架构声明 ↔ 该行引用的 job 真正覆盖的架构
#
# `Windows x64/arm64` 引用 `ci:compile-windows` 看着没问题，而那个 job 只 publish
# `win-x64`——arm64 那一格是空的。引用存在**不等于**覆盖对，这条专门抓这个。
$matrixRows = [regex]::Matches($readmeText, '(?m)^\|\s*([^|]+?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*([^|]*?)\s*\|\s*$')

$checkedRows = 0
foreach ($row in $matrixRows) {
    $platform = $row.Groups[1].Value.Trim()
    $status = $row.Groups[3].Value.Trim()
    $evidence = $row.Groups[4].Value.Trim()

    # 表头与分隔行跳过
    if ($platform -in @('平台', '') -or $platform -match '^-+$') { continue }

    # 矩阵行的平台列形如 `Windows x64/arm64`、`Linux arm64`、`macOS x64`
    if ($platform -notmatch '^(Windows|Linux|macOS)\b') { continue }

    $rowRefs = @([regex]::Matches($evidence, '`ci:([a-z0-9][a-z0-9-]*)`') |
        ForEach-Object { $_.Groups[1].Value })

    if ($rowRefs.Count -eq 0) {
        # 没有证据的行：允许存在（如实降级就是"这行没有该架构的 CI 证据"），
        # 但不允许一边说"真机验证"一边没有任何引用可查。
        #
        # 判据是"含『真机』且不含『未』"：`✅ 真机验证（…）` 命中（要求引用），
        # `⚠️ 仅 IL 交叉编译；未在 arm64 真机运行` 不命中（如实声明未验证）。
        # 不用"含『真机验证』"这个确切串，是因为它会被人换个说法（"真机已验证"）绕过。
        if ($status -match '真机' -and $status -notmatch '未') {
            throw (("README 的「{0}」行声称「{1}」，却没有任何 `ci:<job>` 引用可查。" +
                '要么补上证据引用，要么把状态降级为如实的描述。') -f $platform, $status)
        }

        continue
    }

    $checkedRows++

    # 该行声明的架构：platform 列里出现的 x64 / arm64
    $rowArches = @()
    if ($platform -match 'arm64') { $rowArches += 'arm64' }
    if ($platform -match 'x64') { $rowArches += 'x64' }
    if ($rowArches.Count -eq 0) { continue }

    # 这些 job 覆盖的 RID。
    #
    # **`compile-*` 与 `smoke-*` 的推断规则不同**，因为语义不同：
    #
    #   * `compile-*` 是 **IL 交叉编译**：目标 RID 完全由 `-r` 指定，runner 无关
    #     （`compile-macos` 刻意跑在 windows-latest 上）。所以只认显式 RID——
    #     若把 runner 的 RID 也算进来，`win-x64` 会"满足" `macOS x64` 那一格，
    #     掩盖"这个 job 其实没编译 osx-x64"。
    #
    #   * `smoke-*` **真的在 runner 上把产物跑起来**，所以 runner 架构本身就是一份
    #     真实的覆盖；`-r` 是附加的（`smoke-macos` 跑在 arm64 上，另外多做一次
    #     osx-x64 的交叉 AOT 发布以覆盖"非本机架构的发布能力"）。所以取**并集**。
    $coveredRids = @()
    foreach ($ref in $rowRefs) {
        $info = $jobInfo[$ref]

        $fromRunner = @()
        if ($ref -match '^smoke-' -and $RunnerRids.ContainsKey($info.RunsOn)) {
            $fromRunner = @($RunnerRids[$info.RunsOn])
        }

        $rids = @($info.ExplicitRids) + $fromRunner

        if ($rids.Count -eq 0) {
            throw ("README 的「{0}」行引用了 job「{1}」，但没能从它推断出任何 RID：" -f
                $platform, $ref) +
                '请在 RunnerRids 表里补上它的 runner，或让 job 显式带 -r。'
        }

        $coveredRids += $rids
    }
    $coveredRids = @($coveredRids | Sort-Object -Unique)

    if ($coveredRids.Count -eq 0) {
        throw ("README 的「{0}」行引用的 job（{1}）都没能推断出 RID：" -f
            $platform, ($rowRefs -join '、')) +
            '请在 RunnerRids 表里补上它们的 runner，或让 job 显式带 -r。'
    }

    foreach ($arch in $rowArches) {
        $cover = @($coveredRids | Where-Object { $_ -match "-$arch$" })
        if ($cover.Count -eq 0) {
            throw ("README 的「{0}」行声明覆盖 {1}，但它引用 job「{2}」实际只覆盖 {3}。" -f
                $platform, $arch, ($rowRefs -join '、'), ($coveredRids -join '、')) +
                "`n这一格是空的——要么补一个跑在 $arch runner 上的 job 并引用它，" +
                '要么把这一行的状态改成如实的描述（例如"仅 IL 交叉编译，未在真机运行"）。'
        }
    }
}

if ($checkedRows -eq 0) {
    throw '平台矩阵里没有一行带 `ci:` 引用——架构覆盖无从校验。'
}

# 6. 反向校验：验证类 job 没被任何一行引用，说明矩阵可能漏了某个平台。
#    2026-10-25 评审前只 Write-Warning：job 从矩阵里掉出去 CI 照样绿——
#    "验证没做"比"验证失败"更难被发现，因为它没有红色。升级为失败。
#    新增的验证 job 要么写进矩阵（README 对应行加 `ci:<job>`），要么改名
#    避开 compile-/smoke- 前缀（那两个前缀就是"平台矩阵证据"的命名空间）。
$verificationJobs = @($jobs | Where-Object { $_ -match '^(compile|smoke)-' })
$undocumented = @($verificationJobs | Where-Object { $_ -notin $references })
if ($undocumented.Count -gt 0) {
    throw (("这些验证 job 没有出现在平台矩阵里：{0}。`n" +
        '把它们加进 README 对应行（`ci:<job>`），或从 CI 里删掉——否则"矩阵"与"实际覆盖"会再次分叉。') -f
        ($undocumented -join '、'))
}

Write-Output ("OK：README 的 {0} 个 ci: 引用都能在 ci.yml 里找到（共 {1} 个 job）；" -f
    $references.Count, $jobs.Count)
Write-Output ("    {0} 个矩阵行的架构声明与 job 的 runner 一致。" -f $checkedRows)
