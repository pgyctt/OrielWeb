#Requires -Version 7
<#
.SYNOPSIS
    验证 OrielWeb 的包内 build logic（buildTransitive/OrielWeb.targets）。

.DESCRIPTION
    「包内 MSBuild build logic」不是新能力，而是消灭一个现有缺陷：
    消费方手写 <EmbeddedResource Include="wwwroot\**\*" /> 而不写 LogicalName 时，
    MSBuild 会把目录分隔符压成 '.'，库只能靠"最后一个 '.' 是扩展名"反推目录——
    文件名主干含 '.' 时（app.min.js）必然推错，而且是**静默**的：
    解压不报错，页面按原 URL 请求就是 404 白屏（见 docs/reviews/2026-10-01.md §4.3）。

    本脚本把整条「打包 → 消费 → 断言」的链路跑一遍：

      场景 A 零配置     samples/OrielMinimal（csproj 里一行 EmbeddedResource 都没有）
                        → 资源名必须是显式分隔符形式：OrielMinimal.wwwroot/app.min.js
      场景 B 旧写法     同一个工程加回手写的 <EmbeddedResource Include="wwwroot\**\*" />
                        → 必须仍是兼容反推形式，且**不能**同时出现显式形式
                          （同时出现就说明 targets 没跳过、同一批文件被嵌了两次）
      场景 C 显式关闭   同一个工程加 -p:OrielWebEmbeddedAssets=false
                        → 一条 wwwroot 资源都不该有

.PARAMETER NoPack
    跳过 dotnet pack，直接用 dist/nuget 里现成的包（迭代调试用）。

.PARAMETER KeepTemp
    保留临时目录，便于查看中间产物。

.EXAMPLE
    pwsh tools/verify-pack.ps1
#>
[CmdletBinding()]
param(
    [switch] $NoPack,
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
$temp = Join-Path $repoRoot ('obj/verify-pack/' + [guid]::NewGuid().ToString('N').Substring(0, 8))

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

# 资源名里的目录分隔符在 Windows 上是 '\'（%(RecursiveDir) 的产物），
# 断言一律归一成 '/' 再比，免得同一件事在三个平台上写成三种期望值。
function ConvertTo-NormalizedNames {
    param([string[]] $Names)
    return @($Names | ForEach-Object { $_.Replace('\', '/') })
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

    $output = @(& dotnet $dll)
    if ($LASTEXITCODE -ne 0) {
        throw "$Label：运行产物失败（退出码 $LASTEXITCODE）。"
    }

    return [pscustomobject]@{
        Resources = @(ConvertTo-NormalizedNames -Names @($output | Where-Object { $_ -match '\S' }))
        BuildLog  = $buildLog
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
    # 干净的包缓存：同一个版本号重复 pack 时，NuGet 会拿旧的还原结果，
    # 于是"改了 targets 却没生效"会伪装成通过。
    $env:NUGET_PACKAGES = Join-Path $temp 'packages'
    New-Item -ItemType Directory -Force -Path $env:NUGET_PACKAGES, $temp | Out-Null

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
    Test-Assert ($a.Resources -contains 'OrielMinimal.wwwroot/index.html') `
        '普通文件 index.html 正常嵌入'
    Test-Assert (-not ($a.Resources -contains 'OrielMinimal.wwwroot.app.min.js')) `
        '没有出现兼容反推形式的资源名'
    Test-Assert ($a.BuildLog -match '已自动内嵌') `
        '构建日志里能看到"已自动内嵌"的说明'

    # ── 场景 B：旧写法保持兼容 ──────────────────────────────────────
    Write-Host ''
    Write-Host '--- 4. 场景 B：旧写法（手写 EmbeddedResource，不写 LogicalName）---' -ForegroundColor Cyan
    $legacyDir = Join-Path $temp 'legacy'
    New-SampleCopy -TargetDir $legacyDir

    $legacyCsproj = Join-Path $legacyDir 'OrielMinimal.csproj'
    $legacyItem = @'
  <ItemGroup>
    <!-- 旧写法：不写 LogicalName -->
    <EmbeddedResource Include="wwwroot\**\*" />
  </ItemGroup>

</Project>
'@
    $csprojText = Get-Content $legacyCsproj -Raw -Encoding utf8
    Set-Content -Path $legacyCsproj -NoNewline -Encoding utf8 -Value $csprojText.Replace('</Project>', $legacyItem)

    $b = Invoke-SampleBuild -ProjectDir $legacyDir -Label '场景 B'
    Show-Resources -Names $b.Resources

    Test-Assert ($b.Resources -contains 'OrielMinimal.wwwroot.app.min.js') `
        '旧写法仍是兼容反推形式（行为完全没变）'
    Test-Assert (-not ($b.Resources -contains 'OrielMinimal.wwwroot/app.min.js')) `
        '没有同时嵌一份显式形式（证明 targets 检测到已声明并跳过，没有重复嵌入）'
    Test-Assert ($b.BuildLog -match '跳过自动内嵌') `
        '构建日志里能看到"跳过自动内嵌"的说明'

    # ── 场景 C：显式关闭 ────────────────────────────────────────────
    Write-Host ''
    Write-Host '--- 5. 场景 C：OrielWebEmbeddedAssets=false ---' -ForegroundColor Cyan
    $offDir = Join-Path $temp 'off'
    New-SampleCopy -TargetDir $offDir

    $c = Invoke-SampleBuild -ProjectDir $offDir -Label '场景 C' `
        -ExtraArguments @('-p:OrielWebEmbeddedAssets=false')
    Show-Resources -Names $c.Resources

    Test-Assert (-not ($c.Resources | Where-Object { $_ -like 'OrielMinimal.wwwroot*' })) `
        '关掉之后一条 wwwroot 资源都不嵌入'
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

Write-Host '验证通过：包内 build logic 按约定工作，旧写法保持兼容。' -ForegroundColor Green
exit 0
