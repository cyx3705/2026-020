[CmdletBinding()]
param(
    # 宿主快照根。缺省按"本仓与 2026-023-HistoryVulcan 同库根"的相对路径推导；
    # 从 AI 工作树运行时工作树在库根之外，该相对路径必然指空，由调用方显式传入。
    [string]$HistoryVulcanPackageRoot = $env:HISTORYVULCAN_PACKAGE_ROOT
)

$ErrorActionPreference = 'Stop'

# HistoryJanus 日常质量门禁（VERIFY-FAST 组成部分）。
# 定位：代码管道化条件 4 —— 漂移由日常检查自动阻断，而不是积累到正式发布才暴露。
# 权威源上游：JanusVersion.props（版本）、module.manifest.json（模块身份）、
# z-HistoryJanus（正式树边界）、2026-023-HistoryVulcan z 级快照（宿主合同）。
#
# 注意：所有收集结果必须经 @(...) 包装；单个违规项在 Windows PowerShell 5.1 下是标量，
# 直接读 .Count 会得到 $null 并静默绕过失败分支（2026-08 在 HistoryVulcan 同类脚本中实证）。

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$componentRoot = Join-Path $root 'b-Code-Studio'
$activeRoots = @('b-Code-Studio', 'b-Code-Verify')
$excluded = '\\(bin|obj|Unused|b-Publish|z-HistoryJanus)\\'

$violations = [System.Collections.Generic.List[string]]::new()

# --- 0. 根项目合同：AI 入口、项目身份和版本必须可执行 -------------------------------
$projectManifestPath = Join-Path $root 'project.manifest.json'
$agentsPath = Join-Path $root 'AGENTS.md'
if (-not (Test-Path -LiteralPath $projectManifestPath -PathType Leaf)) {
    $violations.Add('Root project.manifest.json is missing')
}
if (-not (Test-Path -LiteralPath $agentsPath -PathType Leaf)) {
    $violations.Add('Root AGENTS.md is missing')
}

# --- 1. 抑制标记零容忍：NoWarn/SuppressMessage/#pragma disable 一律不得入库。
#        唯一豁免：工程文件中仅抑制 XML 文档警告 CS1573/CS1591 的 NoWarn 行 -------------
$suppressionPattern = 'NoWarn|SuppressMessage|#pragma\s+warning\s+disable'
$docWarningWhitelist = @('CS1573', 'CS1591')
foreach ($relativeRoot in $activeRoots) {
    $path = Join-Path $root $relativeRoot
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $files = Get-ChildItem -LiteralPath $path -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.props', '.targets' -and $_.FullName -notmatch $excluded }
    foreach ($file in $files) {
        $lineNumber = 0
        foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
            $lineNumber++
            if ($line -notmatch $suppressionPattern) { continue }
            $codes = @([regex]::Matches($line, '[A-Z]{2}\d{4}') | ForEach-Object Value)
            $effective = @($codes | Where-Object { $_ -notin $docWarningWhitelist })
            if ($line -match 'NoWarn' -and $effective.Count -eq 0) { continue }
            $violations.Add("Suppression token: $($file.FullName):$lineNumber")
        }
    }
}

# --- 2. 生产代码行数上限（与平台 1000 行一致；超出即按职责拆分） ------------------------
$hotspots = @(
    Get-ChildItem -LiteralPath $componentRoot -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.xaml' -and $_.FullName -notmatch $excluded } |
        ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Lines = ([IO.File]::ReadAllLines($_.FullName)).Count } } |
        Where-Object Lines -gt 1000 |
        Sort-Object Lines -Descending
)
foreach ($hotspot in $hotspots) {
    $violations.Add("Hotspot over 1000 lines: $($hotspot.Path) ($($hotspot.Lines) lines); split by responsibility.")
}

# --- 3. 版本身份链一致：props -> manifest -> 技术合同现行声明 ----------------------------
$versionPropsPath = Join-Path $componentRoot 'JanusVersion.props'
[xml]$versionProps = [IO.File]::ReadAllText($versionPropsPath)
$sourceVersion = [string]$versionProps.Project.PropertyGroup.HistoryJanusVersion
if ([string]::IsNullOrWhiteSpace($sourceVersion)) {
    $violations.Add("JanusVersion.props does not declare HistoryJanusVersion")
}
# 宿主契约版本的唯一真源同样是 JanusVersion.props，脚本不再各自硬编码字面量。
$minimumVulcan = [string]$versionProps.Project.PropertyGroup.MinimumHistoryVulcanVersion
if ([string]::IsNullOrWhiteSpace($minimumVulcan)) {
    $violations.Add("JanusVersion.props does not declare MinimumHistoryVulcanVersion")
}

if (Test-Path -LiteralPath $projectManifestPath -PathType Leaf) {
    $projectManifest = [IO.File]::ReadAllText($projectManifestPath) | ConvertFrom-Json
    if ([string]$projectManifest.project.id -ne '2026-020' -or
        [string]$projectManifest.project.name -ne 'HistoryJanus') {
        $violations.Add('project.manifest.json identity must be 2026-020/HistoryJanus')
    }
    if ([string]$projectManifest.project.version -ne $sourceVersion) {
        $violations.Add("project.manifest.json version $($projectManifest.project.version) != JanusVersion.props $sourceVersion")
    }
}

$manifestPath = Join-Path $componentRoot 'Module\module.manifest.json'
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ([string]$manifest.version -ne $sourceVersion) {
    $violations.Add("module.manifest.json version $($manifest.version) != JanusVersion.props $sourceVersion")
}

$contractPath = Join-Path $root 'b-Office\current\技术合同.md'
$contractText = [IO.File]::ReadAllText($contractPath)
if ($contractText -notmatch "当前开发版本为 ``?$([regex]::Escape($sourceVersion))``?") {
    $violations.Add("技术合同.md 未声明当前开发版本 $sourceVersion")
}

# 运行时 Status 必须是程序集投影，不允许版本字面量回流进源码
$statusSource = [IO.File]::ReadAllText((Join-Path $componentRoot 'Module\HistoryJanusCommands.cs'))
if ($statusSource -match '"[^"]*\d+\.\d+\.\d+[^"]*"') {
    $violations.Add('HistoryJanusCommands.cs contains a hardcoded version literal; project from the assembly instead')
}

# --- 4. Git 与规则交互：产品不自决超时，规则只在离页时批量保存 ----------------------------
$gitRunnerText = [IO.File]::ReadAllText((Join-Path $componentRoot 'Git\GitRunner.cs'))
if ($gitRunnerText -match 'timeoutSeconds|CancellationTokenSource\(TimeSpan|git 命令超时') {
    $violations.Add('GitRunner.cs must not impose an application wall-clock timeout')
}
if ($gitRunnerText -notmatch 'WaitForExitAsync\(cancellation\)' -or
    $gitRunnerText -notmatch 'Kill\(entireProcessTree: true\)') {
    $violations.Add('GitRunner.cs must retain caller cancellation and whole-process-tree cleanup')
}
foreach ($relativePath in @('Git\ProjectService.Commit.cs', 'Git\BranchHistoryService.cs')) {
    $sourceText = [IO.File]::ReadAllText((Join-Path $componentRoot $relativePath))
    if ($sourceText -match 'timeoutSeconds\s*:') {
        $violations.Add("Product Git call site still declares a wall-clock timeout: $relativePath")
    }
}

# 5.0.0（DEC-022）：规则页是一份全库共用清单的编辑器，没有按项目草稿，
# 原先的离页保存/延迟保存断言描述的是已不存在的设计。改为守住新形态：
# 写入只经命令总线（确认归宿主），且不得复活任何延迟保存机制或自建弹窗。
$ruleViewText = [IO.File]::ReadAllText((Join-Path $componentRoot 'Views\ProjectOperationsView.Rules.cs'))
if ($ruleViewText -match 'DispatcherTimer|ScheduleRuleAutoSave|SaveRulesOnPageLeaveAsync|_ruleSaveTask' -or
    $ruleViewText -match 'MessageBox\.Show' -or
    $ruleViewText -notmatch 'SaveExcludeListAsync' -or
    $ruleViewText -notmatch 'ProjectOperationCommandBuilder\.Excludes') {
    $violations.Add('Exclude list edits must go through the bus command builder with no deferred-save machinery')
}

$gitHubRunnerText = [IO.File]::ReadAllText((Join-Path $componentRoot 'GitHub\ToolProcessRunner.cs'))
$smokeRunnerText = [IO.File]::ReadAllText((Join-Path $root 'b-Code-Verify\Smoke\SmokeRunner.cs'))
if ($gitHubRunnerText -notmatch 'timeoutSeconds' -or $smokeRunnerText -notmatch 'SuiteTimeout') {
    $violations.Add('GitHub diagnostics and Smoke hang-detection timeouts must remain explicit')
}

# --- 5. 消费合同投影：API 版本、窗口和命令必须与当前源码事实一致 --------------------------
$apiPath = Join-Path $root 'b-Office\package\模块API.md'
$apiText = [IO.File]::ReadAllText($apiPath)
if ($apiText -notmatch "(?m)^# HistoryJanus $([regex]::Escape($sourceVersion)) 模块 API$") {
    $violations.Add("模块API.md 标题版本未对齐 $sourceVersion")
}
if ($apiText -notmatch "(?m)^- 版本：``$([regex]::Escape($sourceVersion))``。$") {
    $violations.Add("模块API.md 正式消费版本未对齐 $sourceVersion")
}
# 文档只需声明一个宿主基线版本，不再要求与钉版本逐字相等：
# 基线是「我对着哪一版验证的」这一事实，宿主升版不该逼着每个模块改文档。
if ($apiText -notmatch "(?m)^- 宿主基线：HistoryVulcan ``\d+\.\d+\.\d+`` ") {
    $violations.Add("模块API.md 未声明宿主基线版本")
}

$uiSource = [IO.File]::ReadAllText((Join-Path $componentRoot 'Module\HistoryJanusUiModule.cs'))
$sourceWindows = @(
    [regex]::Matches($uiSource, '(?m)^\s*Id\s*=\s*"(?<id>[a-z][a-z0-9]*)"') |
        ForEach-Object { $_.Groups['id'].Value }
)
if ($sourceWindows.Count -ne 3) {
    $violations.Add("HistoryJanusUiModule.cs must declare exactly 3 windows; found $($sourceWindows.Count)")
}

$windowSection = [regex]::Match($apiText, '(?ms)^## UI 窗口\s*(?<body>.*?)(?=^##\s|\z)')
$apiWindows = @(
    [regex]::Matches($windowSection.Groups['body'].Value, '(?m)^\|\s*`(?<id>[a-z][a-z0-9]*)`\s*\|') |
        ForEach-Object { $_.Groups['id'].Value }
)
if (($sourceWindows -join ',') -cne ($apiWindows -join ',')) {
    $violations.Add("模块API.md 窗口清单 [$($apiWindows -join ', ')] != 源码 [$($sourceWindows -join ', ')]")
}

$businessCommandNames = @(
    Get-ChildItem -LiteralPath $componentRoot -Recurse -Filter '*.cs' -File |
        Where-Object { $_.FullName -notmatch $excluded } |
        ForEach-Object {
            $sourceText = [IO.File]::ReadAllText($_.FullName)
            [regex]::Matches($sourceText, '(?m)^\s*Name\s*=\s*"(?<name>janus\.[a-z0-9]+\.[a-z0-9]+)"') |
                ForEach-Object { $_.Groups['name'].Value }
        }
)
$businessCommandNames = @($businessCommandNames | Sort-Object -Unique)
$expectedRuntimeCommandNames = @($businessCommandNames + 'janus.status' | Sort-Object -Unique)
$apiCommandNames = @(
    [regex]::Matches($apiText, '(?m)^\|\s*`(?<name>janus(?:\.[a-z0-9]+){1,2})`\s*\|') |
        ForEach-Object { $_.Groups['name'].Value } |
        Sort-Object -Unique
)
if ($businessCommandNames.Count -ne 35 -or $expectedRuntimeCommandNames.Count -ne 36) {
    $violations.Add("运行时命令总数应为 36（35 条业务命令 + janus.status）；源码为 $($businessCommandNames.Count) + 1")
}
if (($expectedRuntimeCommandNames -join ',') -cne ($apiCommandNames -join ',')) {
    $violations.Add("模块API.md 命令清单与源码不一致：API $($apiCommandNames.Count)，运行时 $($expectedRuntimeCommandNames.Count)")
}

# --- 6. 正式树边界（QA-004 日常化）：运行四件 + 可选 docs/*.md -----------------------------
$packageRoot = Join-Path $root 'z-HistoryJanus'
if (Test-Path -LiteralPath $packageRoot) {
    $allowed = @('HistoryJanus.dll', 'HistoryJanus.xml', 'module.manifest.json', 'SHA256SUMS', 'docs')
    $unexpected = @(
        Get-ChildItem -LiteralPath $packageRoot |
            Where-Object { $_.Name -notin $allowed }
    )
    foreach ($item in $unexpected) {
        $violations.Add("Unexpected entry in z-HistoryJanus: $($item.Name)")
    }
    $formalManifestPath = Join-Path $packageRoot 'module.manifest.json'
    if (-not (Test-Path -LiteralPath $formalManifestPath -PathType Leaf)) {
        $violations.Add('z-HistoryJanus/module.manifest.json is missing')
    }
    $docsRoot = Join-Path $packageRoot 'docs'
    if (Test-Path -LiteralPath $docsRoot) {
        if (-not (Test-Path -LiteralPath $docsRoot -PathType Container)) {
            $violations.Add('z-HistoryJanus/docs must be a directory of Markdown')
        }
        else {
            foreach ($item in @(Get-ChildItem -LiteralPath $docsRoot -Recurse -File)) {
                if ([IO.Path]::GetExtension($item.Name) -ne '.md') {
                    $violations.Add("z-HistoryJanus/docs may contain only Markdown: $($item.Name)")
                }
            }
        }
    }
}

# --- 7. 宿主合同预检：发布脚本同源检查日常化 --------------------------------------------
$vulcanRoot = if ([string]::IsNullOrWhiteSpace($HistoryVulcanPackageRoot)) {
    [IO.Path]::GetFullPath((Join-Path $root '..\2026-023-HistoryVulcan\z-HistoryVulcan'))
}
else {
    [IO.Path]::GetFullPath($HistoryVulcanPackageRoot)
}
$vulcanManifestPath = Join-Path $vulcanRoot 'manifest.json'
$vulcanCorePath = Join-Path $vulcanRoot 'host\HistoryVulcan.Core.dll'
if (-not (Test-Path -LiteralPath $vulcanManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $vulcanCorePath -PathType Leaf)) {
    $violations.Add("HistoryVulcan formal snapshot is incomplete: $vulcanRoot")
}
else {
    $vulcanManifest = [IO.File]::ReadAllText($vulcanManifestPath) | ConvertFrom-Json
    if ([string]$vulcanManifest.product -ne 'HistoryVulcan' -or
        [version]$vulcanManifest.version -lt [version]$minimumVulcan) {
        $violations.Add("Janus requires HistoryVulcan >= $minimumVulcan; found $($vulcanManifest.version)")
    }
    $vulcanCore = [Reflection.AssemblyName]::GetAssemblyName($vulcanCorePath)
    if ($vulcanCore.Version -lt [version]"$minimumVulcan.0") {
        $violations.Add("HistoryVulcan.Core is $($vulcanCore.Version), older than minimum $minimumVulcan.0")
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    exit 1
}
Write-Host ("Quality gate passed: suppressions 0; hotspots {0}; version {1}; windows {2}; commands {3}; host HistoryVulcan {4}." -f $hotspots.Count, $sourceVersion, $sourceWindows.Count, $expectedRuntimeCommandNames.Count, $minimumVulcan)
