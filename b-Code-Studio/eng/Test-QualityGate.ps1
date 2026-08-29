[CmdletBinding()]
param(
    # 宿主快照根。缺省按"本仓与 2026-023-HistoryVulcan 同库根"的相对路径推导；
    # 从 AI 工作树运行时工作树在库根之外，该相对路径必然指空，由调用方显式传入。
    [string]$HistoryVulcanPackageRoot = $env:HISTORYVULCAN_PACKAGE_ROOT,
    # 宿主 vulcan.dev.submit 在候选写入 z 前可传入事务 staging 根；
    # 独立运行时为空，改为校验 z-Publish 下的当前版本化候选。
    [string]$CandidateRoot
)

$ErrorActionPreference = 'Stop'

# HistoryJanus 日常质量门禁（VERIFY-FAST 组成部分）。
# 定位：代码管道化条件 4 —— 漂移由日常检查自动阻断，而不是积累到正式发布才暴露。
# 权威源上游：JanusVersion.props（版本）、module.manifest.json（模块身份）、
# z-Publish（正式树边界）、2026-023-HistoryVulcan z 级快照（宿主合同）。
#
# 注意：所有收集结果必须经 @(...) 包装；单个违规项在 Windows PowerShell 5.1 下是标量，
# 直接读 .Count 会得到 $null 并静默绕过失败分支（2026-08 在 HistoryVulcan 同类脚本中实证）。

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$componentRoot = Join-Path $root 'b-Code-Studio'
$activeRoots = @('b-Code-Studio', 'b-Code-Verify')
$excluded = '\\(bin|obj|Unused|z-Publish|z-Publish)\\'

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

# --- 5. 消费合同投影：API 版本、页面描述和命令必须与当前源码事实一致 ----------------------
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
# 5.4.6：rules / history / github 不再是页面——它们收进 projops 的 switch 容器，
# 由控制面板里的轮换选项框切换（REQ-015）。页面因此从六个回到三个。
$pageIds = @('overview', 'graph', 'projops')
foreach ($pageId in $pageIds) {
    if ($uiSource -notmatch ('id = "' + [regex]::Escape($pageId) + '"')) {
        $violations.Add("HistoryJanusUiModule.cs missing descriptive page $pageId")
    }
    if ($apiText -notmatch ('(?m)^\|\s*`' + [regex]::Escape($pageId) + '`\s*\|')) {
        $violations.Add("模块API.md missing descriptive page $pageId")
    }
}
if ($uiSource -notmatch 'schemaVersion = 1' -or $uiSource -notmatch 'tabTarget = "console"') {
    $violations.Add('HistoryJanusUiModule.cs must expose Aurora V1 pages and target graph at console')
}
foreach ($uiCommand in @('janus.ui.describe', 'janus.ui.actions', 'janus.ui.data', 'janus.ui.graphnode', 'janus.ui.refreshrules')) {
    if ($uiSource -notmatch ('Name = "' + [regex]::Escape($uiCommand) + '"') -or
        $apiText -notmatch ('(?m)^\|\s*`' + [regex]::Escape($uiCommand) + '`\s*\|')) {
        $violations.Add("Aurora UI command missing from source or API contract: $uiCommand")
    }
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
# 5.4.6 增加 janus.ui.refreshrules（REQ-015）：40 → 41。
if ($businessCommandNames.Count -ne 40 -or $expectedRuntimeCommandNames.Count -ne 41) {
    $violations.Add("运行时命令总数应为 41（35 条业务命令 + 5 条 Aurora UI 投影命令 + janus.status）；源码为 $($businessCommandNames.Count) + 1")
}
if (($expectedRuntimeCommandNames -join ',') -cne ($apiCommandNames -join ',')) {
    $violations.Add("模块API.md 命令清单与源码不一致：API $($apiCommandNames.Count)，运行时 $($expectedRuntimeCommandNames.Count)")
}

# --- 6. 候选边界（QA-004 日常化）：事务候选或版本化运行包 + docs/*.md + 独立 history ---
# 普通模块发布形状是 z-Publish/HistoryJanus-v<version>/；构建脚本的
# -OutputRoot 只接收事务 staging 根，不能据此把正式消费根误判成平铺包。
$packageRoot = Join-Path $root 'z-Publish'
$inspectPublishedRoot = [string]::IsNullOrWhiteSpace($CandidateRoot)
if (-not $inspectPublishedRoot) {
    $candidatePath = [IO.Path]::GetFullPath($CandidateRoot)
    if (-not (Test-Path -LiteralPath $candidatePath -PathType Container)) {
        $violations.Add("CandidateRoot is missing: $candidatePath")
    }
    else {
        $candidatePrefix = $candidatePath.TrimEnd('\') + '\'
        $candidateFiles = @(Get-ChildItem -LiteralPath $candidatePath -Recurse -File -Force |
            ForEach-Object { $_.FullName.Substring($candidatePrefix.Length).Replace('\', '/') } |
            Sort-Object)
        $runtimeFiles = @('HistoryJanus.dll', 'HistoryJanus.xml', 'module.manifest.json', 'SHA256SUMS')
        $docsRoot = Join-Path $candidatePath 'docs'
        $docsFiles = if (Test-Path -LiteralPath $docsRoot -PathType Container) {
            @(Get-ChildItem -LiteralPath $docsRoot -Recurse -File -Force |
                ForEach-Object {
                    if ([IO.Path]::GetExtension($_.Name) -ne '.md') {
                        [void]$violations.Add("CandidateRoot/docs may contain only Markdown: $($_.Name)")
                    }
                    $_.FullName.Substring($candidatePrefix.Length).Replace('\', '/')
                } | Sort-Object)
        }
        else {
            [void]$violations.Add('CandidateRoot/docs is missing')
            @()
        }
        if ($docsFiles.Count -eq 0) {
            [void]$violations.Add('CandidateRoot/docs must contain at least one Markdown document')
        }
        $expectedFiles = @($runtimeFiles + $docsFiles) | Sort-Object
        if (($candidateFiles -join "`n") -cne ($expectedFiles -join "`n")) {
            $violations.Add("CandidateRoot file set is invalid: $($candidateFiles -join ', ')")
        }
        foreach ($file in $runtimeFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $candidatePath $file) -PathType Leaf)) {
                $violations.Add("CandidateRoot/$file is missing")
            }
        }

        $manifestPath = Join-Path $candidatePath 'module.manifest.json'
        if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
            try {
                $candidateManifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
                if ([string]$candidateManifest.name -cne 'HistoryJanus' -or
                    [string]$candidateManifest.version -cne $sourceVersion) {
                    $violations.Add("CandidateRoot manifest does not match HistoryJanus $sourceVersion")
                }
            }
            catch {
                $violations.Add('CandidateRoot/module.manifest.json is invalid JSON')
            }
        }

        $sumsPath = Join-Path $candidatePath 'SHA256SUMS'
        if (Test-Path -LiteralPath $sumsPath -PathType Leaf) {
            $hashes = @{}
            foreach ($line in [IO.File]::ReadAllLines($sumsPath)) {
                if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<file>.+)$') {
                    $violations.Add("Invalid CandidateRoot checksum line: $line")
                    continue
                }
                $hashFile = $Matches['file']
                if ($hashes.ContainsKey($hashFile)) {
                    $violations.Add("Duplicate CandidateRoot checksum entry: $hashFile")
                    continue
                }
                $hashes[$hashFile] = $Matches['hash'].ToUpperInvariant()
            }
            $hashTargets = @($candidateFiles | Where-Object { $_ -ne 'SHA256SUMS' } | Sort-Object)
            if ((($hashes.Keys | Sort-Object) -join "`n") -ne (($hashTargets | Sort-Object) -join "`n")) {
                $violations.Add('CandidateRoot SHA256SUMS does not cover exactly the candidate files')
            }
            foreach ($relative in $hashTargets) {
                $path = Join-Path $candidatePath $relative.Replace('/', '\')
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
                $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
                if (-not $hashes.ContainsKey($relative) -or $hashes[$relative] -ne $actualHash) {
                    $violations.Add("CandidateRoot checksum mismatch: $relative")
                }
            }
        }
        $assemblyPath = Join-Path $candidatePath 'HistoryJanus.dll'
        if (Test-Path -LiteralPath $assemblyPath -PathType Leaf) {
            try {
                $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version
                if ($assemblyVersion.ToString() -ne "$sourceVersion.0") {
                    $violations.Add("CandidateRoot assembly version $assemblyVersion != $sourceVersion.0")
                }
            }
            catch {
                $violations.Add('CandidateRoot/HistoryJanus.dll is not a readable .NET assembly')
            }
        }
    }
}
if ($inspectPublishedRoot -and (Test-Path -LiteralPath $packageRoot)) {
    $candidateNamePattern = '^HistoryJanus-v\d+\.\d+\.\d+$'
    $rootEntries = @(Get-ChildItem -LiteralPath $packageRoot -Force)
    $unexpected = @($rootEntries | Where-Object {
        $_.Name -ne 'history' -and
        -not ($_.PSIsContainer -and $_.Name -match $candidateNamePattern)
    })
    foreach ($item in $unexpected) {
        $violations.Add("Unexpected entry in z-Publish root: $($item.Name)")
    }
    $candidates = @(Get-ChildItem -LiteralPath $packageRoot -Directory -Force |
        Where-Object { $_.Name -match $candidateNamePattern })
    if ($candidates.Count -gt 1) {
        $violations.Add("z-Publish must contain at most one current HistoryJanus candidate; found $($candidates.Count)")
    }
    if ($candidates.Count -eq 1) {
        $candidateRoot = $candidates[0].FullName
        $expectedCandidateName = "HistoryJanus-v$sourceVersion"
        if ($candidates[0].Name -cne $expectedCandidateName) {
            $violations.Add("Current Janus candidate $($candidates[0].Name) != $expectedCandidateName")
        }

        $candidatePrefix = $candidateRoot.TrimEnd('\') + '\'
        $candidateFiles = @(Get-ChildItem -LiteralPath $candidateRoot -Recurse -File -Force |
            ForEach-Object { $_.FullName.Substring($candidatePrefix.Length).Replace('\', '/') } |
            Sort-Object)
        $runtimeFiles = @('HistoryJanus.dll', 'HistoryJanus.xml', 'module.manifest.json', 'SHA256SUMS')
        $docsRoot = Join-Path $candidateRoot 'docs'
        if (-not (Test-Path -LiteralPath $docsRoot -PathType Container)) {
            $violations.Add("$expectedCandidateName/docs must be a directory of Markdown")
            $docsFiles = @()
        }
        else {
            $docsFiles = @(Get-ChildItem -LiteralPath $docsRoot -Recurse -File -Force |
                ForEach-Object {
                    if ([IO.Path]::GetExtension($_.Name) -ne '.md') {
                        [void]$violations.Add("$expectedCandidateName/docs may contain only Markdown: $($_.Name)")
                    }
                    $_.FullName.Substring($candidatePrefix.Length).Replace('\', '/')
                } | Sort-Object)
            if ($docsFiles.Count -eq 0) {
                [void]$violations.Add("$expectedCandidateName/docs must contain at least one Markdown document")
            }
        }
        $allowedFiles = @($runtimeFiles + $docsFiles)
        foreach ($file in @($candidateFiles | Where-Object { $_ -notin $allowedFiles })) {
            $violations.Add("Unexpected file in $expectedCandidateName candidate: $file")
        }
        foreach ($file in $runtimeFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $candidateRoot $file) -PathType Leaf)) {
                $violations.Add("$expectedCandidateName/$file is missing")
            }
        }

        $candidateManifestPath = Join-Path $candidateRoot 'module.manifest.json'
        if (Test-Path -LiteralPath $candidateManifestPath -PathType Leaf) {
            try {
                $candidateManifest = [IO.File]::ReadAllText($candidateManifestPath) | ConvertFrom-Json
                if ([string]$candidateManifest.name -cne 'HistoryJanus' -or
                    [string]$candidateManifest.version -cne $sourceVersion) {
                    $violations.Add("$expectedCandidateName/module.manifest.json identity does not match $sourceVersion")
                }
            }
            catch {
                $violations.Add("$expectedCandidateName/module.manifest.json is invalid JSON")
            }
        }

        $sumsPath = Join-Path $candidateRoot 'SHA256SUMS'
        if (Test-Path -LiteralPath $sumsPath -PathType Leaf) {
            $hashes = @{}
            foreach ($line in [IO.File]::ReadAllLines($sumsPath)) {
                if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<file>.+)$') {
                    $violations.Add("Invalid checksum line in ${expectedCandidateName}: $line")
                    continue
                }
                $hashFile = $Matches['file']
                if ($hashes.ContainsKey($hashFile)) {
                    $violations.Add("Duplicate checksum entry in ${expectedCandidateName}: $hashFile")
                    continue
                }
                $hashes[$hashFile] = $Matches['hash'].ToUpperInvariant()
            }
            $hashedFiles = @($candidateFiles | Where-Object { $_ -ne 'SHA256SUMS' } | Sort-Object)
            if ((($hashes.Keys | Sort-Object) -join "`n") -ne (($hashedFiles | Sort-Object) -join "`n")) {
                $violations.Add("$expectedCandidateName/SHA256SUMS does not cover exactly the candidate files")
            }
            foreach ($relative in $hashedFiles) {
                $path = Join-Path $candidateRoot $relative.Replace('/', '\')
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
                $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
                if (-not $hashes.ContainsKey($relative) -or $hashes[$relative] -ne $actualHash) {
                    $violations.Add("$expectedCandidateName checksum mismatch: $relative")
                }
            }
        }
    }
}

# --- 7. 宿主合同预检：发布脚本同源检查日常化 --------------------------------------------
$vulcanRoot = if ([string]::IsNullOrWhiteSpace($HistoryVulcanPackageRoot)) {
    [IO.Path]::GetFullPath((Join-Path $root '..\2026-023-HistoryVulcan\z-Publish'))
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
Write-Host ("Quality gate passed: suppressions 0; hotspots {0}; version {1}; pages {2}; commands {3}; host HistoryVulcan {4}." -f $hotspots.Count, $sourceVersion, $pageIds.Count, $expectedRuntimeCommandNames.Count, $minimumVulcan)
