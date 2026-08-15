[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    # 宿主快照根。缺省按"本仓与 2026-023-HistoryVulcan 同库根"的相对路径推导；
    # 从 AI 工作树构建时工作树在库根之外，该相对路径必然指空，由调用方显式传入。
    [string]$HistoryVulcanPackageRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$componentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $componentRoot '..'))
$publishRoot = Join-Path $repoRoot 'b-Publish'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $publishRoot 'current\HistoryJanus'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $OutputRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside the HistoryJanus project: $OutputRoot"
}

$moduleProject = Join-Path $componentRoot 'Module\HistoryJanus.Module.csproj'
$moduleManifestSource = Join-Path $componentRoot 'Module\module.manifest.json'
$apiDocuments = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'b-Office\package') -Filter '*.md' -File)
if ($apiDocuments.Count -ne 1) {
    throw 'b-Office/package must contain exactly one API Markdown document'
}
$apiDocumentSource = $apiDocuments[0].FullName
$apiDocumentName = $apiDocuments[0].Name
$historyVulcanRoot = if ([string]::IsNullOrWhiteSpace($HistoryVulcanPackageRoot)) {
    [IO.Path]::GetFullPath((Join-Path $repoRoot '..\2026-023-HistoryVulcan\z-HistoryVulcan'))
}
else {
    [IO.Path]::GetFullPath($HistoryVulcanPackageRoot)
}
$workRoot = Join-Path $publishRoot 'work'
$transactionId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $workRoot "HistoryJanus-candidate-$transactionId"
$backup = Join-Path $workRoot "HistoryJanus-previous-$transactionId"
$failed = Join-Path $workRoot "HistoryJanus-failed-$transactionId"

function Invoke-Dotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-RelativePackagePath {
    param([string]$Path, [string]$Root)

    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package path escapes its root: $fullPath"
    }
    return $fullPath.Substring($prefix.Length).Replace('\', '/')
}

function Assert-ModulePackage {
    param([string]$Root, [string]$ExpectedVersion)

    $expectedFiles = @(
        'HistoryJanus.dll',
        'HistoryJanus.xml',
        'module.manifest.json',
        'SHA256SUMS'
    ) | Sort-Object
    $actualFiles = @(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object {
        Get-RelativePackagePath $_.FullName $Root
    } | Sort-Object)
    if (($actualFiles -join "`n") -ne ($expectedFiles -join "`n")) {
        throw "HistoryJanus package file set is invalid: $($actualFiles -join ', ')"
    }

    $manifest = [IO.File]::ReadAllText((Join-Path $Root 'module.manifest.json'), [Text.UTF8Encoding]::new($false)) |
        ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.type -ne 'HistoryVulcan.Module' -or
        $manifest.name -ne 'HistoryJanus' -or $manifest.version -ne $ExpectedVersion -or
        $manifest.artifact -ne 'HistoryJanus.dll' -or $manifest.docs -ne 'HistoryJanus.xml' -or
        $manifest.mcpExposure -ne 'readonly' -or $manifest.ui -ne $true) {
        throw "HistoryJanus manifest identity does not match $ExpectedVersion"
    }

    $hashes = @{}
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $Root 'SHA256SUMS'), [Text.UTF8Encoding]::new($false))) {
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64})  (?<file>.+)$') {
            throw "Invalid HistoryJanus checksum line: $line"
        }
        if ($hashes.ContainsKey($Matches.file)) {
            throw "Duplicate HistoryJanus checksum entry: $($Matches.file)"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }
    $hashedFiles = @($expectedFiles | Where-Object { $_ -ne 'SHA256SUMS' })
    if ($hashes.Count -ne $hashedFiles.Count) {
        throw 'HistoryJanus checksum does not cover the complete package'
    }
    foreach ($relative in $hashedFiles) {
        $path = Join-Path $Root $relative.Replace('/', '\')
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $hashes.ContainsKey($relative) -or $hashes[$relative] -ne $actual) {
            throw "HistoryJanus checksum mismatch: $relative"
        }
    }

    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $Root 'HistoryJanus.dll'))
    if ($assembly.Version.ToString() -ne "$ExpectedVersion.0") {
        throw "HistoryJanus assembly version is $($assembly.Version), expected $ExpectedVersion.0"
    }
}

$versionOutput = & dotnet msbuild $moduleProject -nologo `
    -getProperty:HistoryJanusVersion -getProperty:MinimumHistoryVulcanVersion
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to evaluate Janus version source'
}
$versionProperties = (($versionOutput -join "`n") | ConvertFrom-Json).Properties
$version = [string]$versionProperties.HistoryJanusVersion
$minimumVulcan = [string]$versionProperties.MinimumHistoryVulcanVersion
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $minimumVulcan -notmatch '^\d+\.\d+\.\d+$') {
    throw 'JanusVersion.props must declare valid HistoryJanus and HistoryVulcan versions'
}

$sourceManifest = [IO.File]::ReadAllText($moduleManifestSource, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
if ($sourceManifest.name -ne 'HistoryJanus' -or $sourceManifest.version -ne $version) {
    throw "HistoryJanus source manifest does not match version $version"
}
$hostManifestPath = Join-Path $historyVulcanRoot 'manifest.json'
$hostCorePath = Join-Path $historyVulcanRoot 'host\HistoryVulcan.Core.dll'
if (-not (Test-Path -LiteralPath $hostManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $hostCorePath -PathType Leaf)) {
    throw "HistoryVulcan formal snapshot is incomplete: $historyVulcanRoot"
}
$hostManifest = [IO.File]::ReadAllText($hostManifestPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
$hostCore = [Reflection.AssemblyName]::GetAssemblyName($hostCorePath)
# 宿主兼容性按**下限**判定，不再要求精确相等。
# 精确钉曾是 Core 会随每个消费方增长时的合理自保；Core 自 3.9.0 冻结后，模块该信的是
# 冻结合同而不是版本号相等。否则宿主每发一版，N 个模块全部被迫改钉、重建、重发，
# 功能上一行不需要动——模块一多这就是瘫痪。
# 真正的兼容性由本仓的加载器 Smoke 验证：把模块装进 ALC 跑一遍。
if ($hostManifest.product -ne 'HistoryVulcan') {
    throw "Not a HistoryVulcan formal snapshot: $historyVulcanRoot"
}
if ([version]$hostManifest.version -lt [version]$minimumVulcan) {
    throw "HistoryJanus $version requires HistoryVulcan >= $minimumVulcan; found $($hostManifest.version)"
}

$promoted = $false
$backedUp = $false
New-Item -ItemType Directory -Force -Path $workRoot, (Split-Path -Parent $OutputRoot) | Out-Null
Push-Location $repoRoot
try {
    Invoke-Dotnet @('restore', 'HistoryJanus.sln', '--locked-mode', '-p:NuGetAudit=false')
    Invoke-Dotnet @('build', 'HistoryJanus.sln', '-c', 'Debug', '--no-restore', '-p:NuGetAudit=false')
    Invoke-Dotnet @('build', 'HistoryJanus.sln', '-c', $Configuration, '--no-restore', '-p:NuGetAudit=false')

    $releaseRoot = Join-Path $componentRoot "Module\bin\$Configuration\net8.0-windows"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryJanus.dll') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'HistoryJanus.xml') -Destination $stage
    Copy-Item -LiteralPath $moduleManifestSource -Destination (Join-Path $stage 'module.manifest.json')
    # 候选只含运行四件。消费 Markdown 由 Diana 发布管线在校验后写入 docs/ 并重写 SHA256SUMS。
    $relativeFiles = @('HistoryJanus.dll', 'HistoryJanus.xml', 'module.manifest.json')
    $checksumLines = foreach ($relative in $relativeFiles) {
        $path = Join-Path $stage $relative.Replace('/', '\')
        "$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)  $relative"
    }
    [IO.File]::WriteAllLines(
        (Join-Path $stage 'SHA256SUMS'),
        $checksumLines,
        [Text.UTF8Encoding]::new($false))
    Assert-ModulePackage $stage $version

    try {
        if (Test-Path -LiteralPath $OutputRoot) {
            Move-Item -LiteralPath $OutputRoot -Destination $backup
            $backedUp = $true
        }
        Move-Item -LiteralPath $stage -Destination $OutputRoot
        $promoted = $true
        Assert-ModulePackage $OutputRoot $version
        if ($backedUp) {
            Remove-Item -LiteralPath $backup -Recurse -Force
            $backedUp = $false
        }
    }
    catch {
        if ($promoted -and (Test-Path -LiteralPath $OutputRoot)) {
            Move-Item -LiteralPath $OutputRoot -Destination $failed
        }
        if ($backedUp -and (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $OutputRoot
        }
        throw
    }
    Write-Host "HistoryJanus $version candidate package created: $OutputRoot"
}
finally {
    & dotnet build-server shutdown | Out-Null
    Pop-Location
    foreach ($path in @($stage, $backup, $failed)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
