[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$OutputDirectory = 'dist',
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$number = '(?:0|[1-9][0-9]*)'
$preIdentifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
$tagPattern = '\Av(?<version>(?<major>' + $number + ')\.(?<minor>' + $number +
    ')\.(?<patch>' + $number + ')(?:-' + $preIdentifier + '(?:\.' + $preIdentifier + ')*)?' +
    '(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)\z'
$tagMatch = [regex]::Match($Tag, $tagPattern)
if (!$tagMatch.Success) {
    throw 'Tag must be a SemVer tag such as v0.1.3 or v0.2.0-rc.1.'
}
$version = $tagMatch.Groups['version'].Value
$versionParts = foreach ($part in @('major', 'minor', 'patch')) {
    $value = [uint32]0
    if (![uint32]::TryParse($tagMatch.Groups[$part].Value, [ref]$value) -or $value -gt 65535) {
        throw 'Each installer version component must be between 0 and 65535.'
    }
    $value
}
$fileVersion = ($versionParts -join '.') + '.0'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw 'OutputDirectory must not be empty.'
}
if (![System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
foreach ($path in @($projectRoot, $outputRoot)) {
    if ($path.Contains('"') -or $path.Contains("`r") -or $path.Contains("`n")) {
        throw 'Installer source and output paths must not contain double quotes or line breaks.'
    }
}

$payloadFiles = @(
    'Screenline.exe', 'Screenline.exe.config', 'README.md', 'LICENSE',
    'THIRD-PARTY-NOTICES.txt', 'CHANGELOG.md', 'docs/WINDOWS.md', 'docs/MIGRATION.md',
    'docs/images/shelf-preview.png', 'docs/images/shelf-empty.png',
    'installer/Screenline.iss', 'installer/Languages/ChineseSimplified.isl'
)
foreach ($relativePath in $payloadFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relativePath) -PathType Leaf)) {
        throw ('Required installer file is missing: ' + $relativePath)
    }
}

# Never turn a stale executable or a failed self-test into a distributable installer.
$reportPath = Join-Path $projectRoot 'tests/results/report.txt'
if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw 'Self-test report is missing. Run .\build.ps1 -Test before building the installer.'
}
$executable = Get-Item -LiteralPath (Join-Path $projectRoot 'Screenline.exe')
if ($executable.VersionInfo.ProductVersion -cne $version) {
    throw ('Screenline.exe version does not match ' + $Tag + '. Run .\build.ps1 -Version ' + $Tag + ' -Test before packaging.')
}
$report = Get-Item -LiteralPath $reportPath
if ($report.LastWriteTimeUtc -lt $executable.LastWriteTimeUtc) {
    throw 'Self-test report is older than Screenline.exe. Run .\build.ps1 -Test again.'
}
$reportLines = @(Get-Content -LiteralPath $reportPath -Encoding UTF8)
$resultLines = @($reportLines | Where-Object { $_ -match '^RESULT:' })
$passCount = @($reportLines | Where-Object { $_ -match '^PASS\s+\S' }).Count
$failCount = @($reportLines | Where-Object { $_ -match '^FAIL\b' }).Count
if ($resultLines.Count -ne 1 -or $resultLines[0] -cne 'RESULT: PASS; failures=0' -or
    $passCount -lt 1 -or $failCount -gt 0) {
    throw 'Self-test report is not a complete successful run (RESULT: PASS; failures=0).'
}

if ([string]::IsNullOrWhiteSpace($IsccPath) -and
    ![string]::IsNullOrWhiteSpace($env:ISCC_PATH)) {
    $IsccPath = $env:ISCC_PATH
}
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $compilerCommand = Get-Command 'ISCC.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $compilerCommand) {
        $IsccPath = $compilerCommand.Source
    }
    else {
        $programFilesX86 = [System.Environment]::GetFolderPath('ProgramFilesX86')
        $candidatePath = Join-Path $programFilesX86 'Inno Setup 6/ISCC.exe'
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) { $IsccPath = $candidatePath }
    }
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or
    !(Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 6.7 or later is required. Install it from https://jrsoftware.org/isdl.php or pass -IsccPath to ISCC.exe.'
}
$compilerPath = (Resolve-Path -LiteralPath $IsccPath).Path
# ISCC.exe is a wrapper around ISCmplr.dll. Its Windows file resource version
# does not identify the compiler engine version. Let the compiler validate the
# script directives instead of rejecting a supported engine based on the wrapper.
Write-Output ('Inno Setup compiler: ' + $compilerPath)

[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$installerPath = Join-Path $outputRoot ('Screenline-' + $Tag + '-windows-x64-setup.exe')
$arguments = @(
    ('/DReleaseTag=' + $Tag), ('/DReleaseVersion=' + $version),
    ('/DFileVersion=' + $fileVersion), ('/DSourceRoot=' + $projectRoot),
    ('/DInstallerOutputDir=' + $outputRoot), (Join-Path $projectRoot 'installer/Screenline.iss')
)
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw ('Inno Setup compilation failed with exit code ' + $LASTEXITCODE + '.')
}
if (!(Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw ('Inno Setup did not produce the expected installer: ' + $installerPath)
}
Write-Output ('Installer: ' + $installerPath)
