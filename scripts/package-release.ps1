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
$utf8 = New-Object System.Text.UTF8Encoding($false)

# SemVer identifiers keep tags safe as file names and git archive prefixes.
$number = '(?:0|[1-9][0-9]*)'
$preIdentifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
$tagPattern = '\Av(?<version>' + $number + '\.' + $number + '\.' + $number +
    '(?:-(?<prerelease>' + $preIdentifier + '(?:\.' + $preIdentifier + ')*))?' +
    '(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)\z'
$tagMatch = [regex]::Match($Tag, $tagPattern)
if (!$tagMatch.Success) {
    throw 'Tag must be a SemVer tag such as v0.1.1 or v0.2.0-rc.1.'
}
$version = $tagMatch.Groups['version'].Value
$prerelease = $tagMatch.Groups['prerelease'].Success.ToString().ToLowerInvariant()

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw 'OutputDirectory must not be empty.'
}
if (![System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($outputRoot.Contains("`r") -or $outputRoot.Contains("`n")) {
    throw 'OutputDirectory must not contain line breaks.'
}

$portableFiles = @(
    'Screenline.exe',
    'Screenline.exe.config',
    'update-source.json',
    'README.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.txt',
    'CHANGELOG.md',
    'docs/WINDOWS.md',
    'docs/MIGRATION.md',
    'docs/images/shelf-preview.png',
    'docs/images/shelf-empty.png'
)
foreach ($relativePath in $portableFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relativePath) -PathType Leaf)) {
        throw ('Required release file is missing: ' + $relativePath)
    }
}

$reportPath = Join-Path $projectRoot 'tests/results/report.txt'
if (!(Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw 'Self-test report is missing. Run .\build.ps1 -Test before packaging.'
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

Get-Command git -ErrorAction Stop | Out-Null
$commitResult = @(& git -C $projectRoot rev-parse --verify 'HEAD^{commit}')
if ($LASTEXITCODE -ne 0 -or $commitResult.Count -ne 1 -or
    $commitResult[0] -notmatch '\A[0-9a-f]{40,64}\z') {
    throw 'Unable to resolve the source commit.'
}
$commit = $commitResult[0]
$sourceChanges = @(& git -C $projectRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or $sourceChanges.Count -gt 0) {
    throw 'Source checkout is not clean. Commit all release source files before packaging.'
}

[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$portableName = 'Screenline-' + $Tag + '-windows-x64.zip'
$sourceName = 'Screenline-' + $Tag + '-source.zip'
$installerName = 'Screenline-' + $Tag + '-windows-x64-setup.exe'
$portablePath = Join-Path $outputRoot $portableName
$sourcePath = Join-Path $outputRoot $sourceName
$installerPath = Join-Path $outputRoot $installerName
$checksumsPath = Join-Path $outputRoot 'SHA256SUMS.txt'
$notesPath = Join-Path $outputRoot 'release-notes.md'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipStream = $null
$archive = $null
try {
    $zipStream = [System.IO.File]::Open($portablePath, [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $archive = New-Object System.IO.Compression.ZipArchive(
        $zipStream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    foreach ($relativePath in $portableFiles) {
        $entry = $archive.CreateEntry(('Screenline/' + $relativePath),
            [System.IO.Compression.CompressionLevel]::Optimal)
        $inputStream = $null
        $entryStream = $null
        try {
            $inputStream = [System.IO.File]::OpenRead((Join-Path $projectRoot $relativePath))
            $entryStream = $entry.Open()
            $inputStream.CopyTo($entryStream)
        }
        finally {
            if ($null -ne $entryStream) { $entryStream.Dispose() }
            if ($null -ne $inputStream) { $inputStream.Dispose() }
        }
    }
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $zipStream) { $zipStream.Dispose() }
}

# git archive includes the exact commit and excludes ignored build/test products.
$archiveArguments = @('-C', $projectRoot, 'archive', '--format=zip',
    ('--prefix=screenline-windows-' + $Tag + '/'), ('--output=' + $sourcePath), $commit)
& git @archiveArguments
if ($LASTEXITCODE -ne 0) { throw 'git archive failed; release packaging is incomplete.' }

& (Join-Path $PSScriptRoot 'build-installer.ps1') -Tag $Tag -OutputDirectory $outputRoot -IsccPath $IsccPath
if (!(Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw 'Installer packaging did not produce the expected EXE.' }

$checksumLines = foreach ($archivePath in @($portablePath, $sourcePath, $installerPath)) {
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $hash + '  ' + [System.IO.Path]::GetFileName($archivePath)
}
[System.IO.File]::WriteAllText($checksumsPath, (($checksumLines -join "`n") + "`n"), $utf8)

$repositoryUrl = 'https://github.com/xiaogao007/screenline-windows'
if (![string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
    $serverUrl = 'https://github.com'
    if (![string]::IsNullOrWhiteSpace($env:GITHUB_SERVER_URL)) {
        $serverUrl = $env:GITHUB_SERVER_URL.TrimEnd('/')
    }
    $repositoryUrl = $serverUrl + '/' + $env:GITHUB_REPOSITORY
}
$tagUrl = [System.Uri]::EscapeDataString($Tag)
$preparedNotesPath = Join-Path $projectRoot ('docs/RELEASE-' + $Tag + '.md')
if (Test-Path -LiteralPath $preparedNotesPath -PathType Leaf) {
    $releaseNotes = [System.IO.File]::ReadAllText($preparedNotesPath, $utf8).TrimEnd()
}
else {
    $releaseNotes = @"
# 截图晾台 $Tag · Windows x64

Windows 原生截图晾台：区域截图、顶部图片卡片、导入与自动收集新截图、复制拖出、预览、画笔标注及撤销移出。

## 下载与运行

- **$installerName**：Windows x64 安装版，支持可选桌面快捷方式、开始菜单和系统卸载。默认安装到当前用户目录，卸载保留截图及设置。
- **$portableName**：Windows 10 / 11 x64 便携程序。完整解压后运行 ``Screenline/Screenline.exe``，保留相邻的 ``Screenline.exe.config``。
- **$sourceName**：本次发布对应提交的完整源码与中文文档。
- **SHA256SUMS.txt**：安装程序和以上两个 ZIP 文件的 SHA-256 校验值。

需要 .NET Framework 4.8。程序和安装包尚未代码签名，没有应用内自动更新。录屏和视频裁剪尚未实现。

``Ctrl + Alt + T`` 展开或收起；``Ctrl + Alt + S`` 区域截图。图片保存在本机；剪贴板后台监听与登录启动默认关闭。

[中文 README]($repositoryUrl/blob/$tagUrl/README.md) · [Windows 使用与常见问题]($repositoryUrl/blob/$tagUrl/docs/WINDOWS.md) · [平台迁移差异]($repositoryUrl/blob/$tagUrl/docs/MIGRATION.md)
"@
}
$releaseNotes += @"


## 本次自动构建

- 版本标签：``$Tag``
- 源码提交：[$commit]($repositoryUrl/commit/$commit)
- 自检：**$passCount 项通过，0 项失败**（来自本次构建的 ``tests/results/report.txt``）。
- 发布附件：``$installerName``、``$portableName``、``$sourceName``、``SHA256SUMS.txt``。
"@
[System.IO.File]::WriteAllText($notesPath, ($releaseNotes.TrimEnd() + "`n"), $utf8)

$outputs = [ordered]@{
    'tag' = $Tag
    'version' = $version
    'commit' = $commit
    'prerelease' = $prerelease
    'portable' = $portablePath
    'source' = $sourcePath
    'installer' = $installerPath
    'checksums' = $checksumsPath
    'notes' = $notesPath
    'artifact-dir' = $outputRoot
}
if (![string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    $outputLines = foreach ($key in $outputs.Keys) { $key + '=' + $outputs[$key] }
    [System.IO.File]::AppendAllText($env:GITHUB_OUTPUT, (($outputLines -join "`n") + "`n"), $utf8)
}
Write-Output ('Packaged ' + $Tag + ' from ' + $commit + '; self-tests passed: ' + $passCount)
Write-Output ('Portable: ' + $portablePath)
Write-Output ('Source: ' + $sourcePath)
Write-Output ('Installer: ' + $installerPath)
Write-Output ('Checksums: ' + $checksumsPath)
Write-Output ('Release notes: ' + $notesPath)
