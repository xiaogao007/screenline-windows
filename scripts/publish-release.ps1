[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Commit,
    [string]$ArtifactDirectory = 'dist',
    [string]$Repository = $env:GITHUB_REPOSITORY
)

$ErrorActionPreference = 'Stop'
$versionPattern = '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
if ($Tag -cnotmatch $versionPattern) { throw 'Invalid version tag.' }
$isPrerelease = ![string]::IsNullOrEmpty($Matches[5])
if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw 'A complete build commit SHA is required.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Set GITHUB_REPOSITORY or pass -Repository owner/name.' }
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GH_TOKEN is required; GitHub Actions supplies GITHUB_TOKEN.' }
if (!(Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI is required on the release runner.' }

$projectRoot = Split-Path -Parent $PSScriptRoot
if (![IO.Path]::IsPathRooted($ArtifactDirectory)) { $ArtifactDirectory = Join-Path $projectRoot $ArtifactDirectory }
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$portableName = "Screenline-$Tag-windows-x64.zip"
$sourceName = "Screenline-$Tag-source.zip"
$installerName = "Screenline-$Tag-windows-x64-setup.exe"
$packageNames = @($portableName, $sourceName, $installerName)
$assetNames = $packageNames + @('SHA256SUMS.txt')
$notesPath = Join-Path $artifactRoot 'release-notes.md'
foreach ($name in $assetNames + @('release-notes.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $artifactRoot $name) -PathType Leaf)) { throw "Missing release file: $name" }
}

# Check both ZIPs and the installer before any GitHub mutation.
$checksumLines = @(Get-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt') -Encoding UTF8 | Where-Object { ![string]::IsNullOrWhiteSpace($_) })
if ($checksumLines.Count -ne $packageNames.Count) { throw 'Expected exactly three package checksum records, including the installer.' }
$verifiedNames = @{}
foreach ($line in $checksumLines) {
    if ($line -cnotmatch '^([0-9a-fA-F]{64})  ([^/\\]+)$') { throw 'Invalid checksum record.' }
    $expectedHash = $Matches[1]
    $name = $Matches[2]
    if ($name -cnotin $packageNames -or $verifiedNames.ContainsKey($name)) { throw 'Unexpected or duplicate checksum filename.' }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $artifactRoot $name)).Hash
    if ($actualHash -ine $expectedHash) { throw "Checksum mismatch: $name" }
    $verifiedNames[$name] = $true
}

function Invoke-Gh {
    param([string[]]$Arguments)
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('GitHub CLI failed: ' + ($Arguments[0..([Math]::Min(1, $Arguments.Length - 1))] -join ' ')) }
    return $result
}

$remoteCommit = (Invoke-Gh -Arguments @('api', "repos/$Repository/commits/$Tag", '--jq', '.sha') | Out-String).Trim()
if ($remoteCommit -cne $Commit) { throw 'The remote tag no longer points to the tested build commit.' }
$checkoutCommit = (& git -C $projectRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $checkoutCommit -cne $Commit) { throw 'The release checkout must match the tested build commit.' }

# Resolve pending tags using the same GraphQL lookup as GitHub CLI. REST list/tag
# endpoints do not reliably expose a newly-created draft by its pending tag name.
function Find-Release {
    $parts = $Repository.Split('/')
    $payload = @{
        query = 'query($owner:String!,$name:String!,$tagName:String!){repository(owner:$owner,name:$name){release(tagName:$tagName){databaseId isDraft}}}'
        variables = @{ owner = $parts[0]; name = $parts[1]; tagName = $Tag }
    }
    $lookupPath = Join-Path $artifactRoot 'release-lookup.json'
    [IO.File]::WriteAllText($lookupPath, ($payload | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    $response = (Invoke-Gh -Arguments @('api', 'graphql', '--input', $lookupPath) | Out-String) | ConvertFrom-Json
    if ($response.errors -or $null -eq $response.data.repository) { throw 'GitHub release lookup failed.' }
    $found = $response.data.repository.release
    if ($null -eq $found) { return $null }
    return ((Invoke-Gh -Arguments @('api', "repos/$Repository/releases/$($found.databaseId)") | Out-String) | ConvertFrom-Json)
}
$existing = Find-Release
if ($null -ne $existing) {
    if (!$existing.draft) { throw "Release $Tag is already published; existing releases are never overwritten. Use a new version tag." }
    if ($existing.target_commitish -cne $Commit) { throw 'The existing draft belongs to a different commit.' }
}
else {
    $create = @('release', 'create', $Tag, '--repo', $Repository, '--verify-tag', '--target', $Commit, '--draft', '--title', "Screenline $Tag - Windows", '--notes-file', $notesPath)
    if ($isPrerelease) { $create += '--prerelease' }
    Invoke-Gh -Arguments $create | Out-Host
}
$draft = $null
for ($attempt = 0; $attempt -lt 5; $attempt++) {
    $draft = Find-Release
    if ($null -ne $draft) { break }
    Start-Sleep -Seconds 2
}
if ($null -eq $draft -or !$draft.draft -or $draft.target_commitish -cne $Commit) { throw 'Cannot verify a draft for the tested commit.' }

# Only unpublished drafts may replace assets on retry.
Invoke-Gh -Arguments @('release', 'edit', $Tag, '--repo', $Repository, '--notes-file', $notesPath) | Out-Host
$upload = @('release', 'upload', $Tag, '--repo', $Repository, '--clobber')
foreach ($name in $assetNames) { $upload += (Join-Path $artifactRoot $name) }
Invoke-Gh -Arguments $upload | Out-Host
$release = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases/$($draft.id)") | Out-String) | ConvertFrom-Json
if (!$release.draft) { throw 'Release was published before upload verification completed.' }
foreach ($name in $assetNames) {
    $assets = @($release.assets | Where-Object { $_.name -ceq $name })
    $localFile = Get-Item -LiteralPath (Join-Path $artifactRoot $name)
    if ($assets.Count -ne 1 -or $assets[0].state -ne 'uploaded' -or $assets[0].size -ne $localFile.Length) { throw "Uploaded asset is missing or incomplete: $name" }
    $digest = 'sha256:' + (Get-FileHash -Algorithm SHA256 -LiteralPath $localFile.FullName).Hash.ToLowerInvariant()
    if ([string]::IsNullOrEmpty($assets[0].digest) -or $assets[0].digest -cne $digest) { throw "Uploaded asset digest mismatch: $name" }
}
$prereleaseFlag = '--prerelease=' + $isPrerelease.ToString().ToLowerInvariant()
Invoke-Gh -Arguments @('release', 'edit', $Tag, '--repo', $Repository, '--draft=false', $prereleaseFlag) | Out-Host
$published = (Invoke-Gh -Arguments @('api', "repos/$Repository/releases/tags/$Tag") | Out-String) | ConvertFrom-Json
if ($published.draft -or $published.target_commitish -cne $Commit) { throw 'Release publication verification failed.' }
Write-Output ('Published ' + $published.html_url)
if (![string]::IsNullOrEmpty($env:GITHUB_STEP_SUMMARY)) {
    ('### Windows Release' + [Environment]::NewLine + [Environment]::NewLine + '[' + $Tag + '](' + $published.html_url + ')' + [Environment]::NewLine) | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
}
