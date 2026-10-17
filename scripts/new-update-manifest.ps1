[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$ArtifactDirectory = 'dist',
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$number = '(?:0|[1-9][0-9]*)'
if ($Tag.Length -gt 256 -or $Tag -cnotmatch ('\Av' + $number + '\.' + $number + '\.' + $number + '(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z')) {
    throw 'Domestic latest manifests accept stable SemVer tags only.'
}
if (![IO.Path]::IsPathRooted($ArtifactDirectory)) { $ArtifactDirectory = Join-Path $projectRoot $ArtifactDirectory }
$artifactRoot = [IO.Path]::GetFullPath($ArtifactDirectory)
$installerName = 'Screenline-' + $Tag + '-windows-x64-setup.exe'
$installerPath = Join-Path $artifactRoot $installerName
if (!(Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw 'Installer asset is missing.' }
$installerSize = (Get-Item -LiteralPath $installerPath).Length
if ($installerSize -lt 2 -or $installerSize -gt (256 * 1024 * 1024)) { throw 'Installer size must be between 2 bytes and 256 MiB.' }
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLines = @(Get-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt') -Encoding UTF8)
$expectedLine = $hash + '  ' + $installerName
if (@($checksumLines | Where-Object { $_ -ceq $expectedLine }).Count -ne 1) { throw 'Installer checksum is missing or does not match.' }
$notesPath = Join-Path $artifactRoot 'release-notes.md'
$notes = [IO.File]::ReadAllText($notesPath, (New-Object Text.UTF8Encoding($false, $true))).Trim()
# The update window displays plain text. Keep prose and link labels without Markdown decoration.
$notes = [regex]::Replace($notes, '\[([^\]]+)\]\([^\)]+\)', '$1')
$notes = [regex]::Replace($notes, '(?m)^#{1,6}\s*', '')
$notes = $notes.Replace('`', '').Replace('**', '')
foreach ($character in $notes.ToCharArray()) {
    if ([char]::IsControl($character) -and $character -ne [char]13 -and $character -ne [char]10 -and $character -ne [char]9) {
        throw 'Release notes contain unsupported control characters.'
    }
}
if ($notes.Length -gt 16384) {
    $notes = $notes.Substring(0, 16383)
    if ([char]::IsHighSurrogate($notes[$notes.Length - 1])) { $notes = $notes.Substring(0, $notes.Length - 1) }
    $notes += [char]0x2026
}
$utf8 = New-Object Text.UTF8Encoding($false)
$payload = [ordered]@{ schema = 1; tag = $Tag; installer = $installerName; size = $installerSize; sha256 = $hash; notes = $notes }
$payloadBytes = $utf8.GetBytes(($payload | ConvertTo-Json -Compress))
if ($payloadBytes.Length -gt (64 * 1024)) { throw 'Signed payload exceeds the 64 KiB client limit. Shorten release notes.' }
if ([string]::IsNullOrWhiteSpace($env:UPDATE_SIGNING_PRIVATE_KEY_XML) -or [string]::IsNullOrWhiteSpace($env:SCREENLINE_UPDATE_PUBLIC_KEY)) {
    throw 'Set UPDATE_SIGNING_PRIVATE_KEY_XML and SCREENLINE_UPDATE_PUBLIC_KEY in the signing environment.'
}
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$publicRsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$publicRsa.PersistKeyInCsp = $false
try {
    $rsa.FromXmlString($env:UPDATE_SIGNING_PRIVATE_KEY_XML)
    $publicRsa.FromXmlString($env:SCREENLINE_UPDATE_PUBLIC_KEY)
    if ($rsa.KeySize -lt 2048 -or $rsa.KeySize -gt 8192 -or $rsa.PublicOnly -or !$publicRsa.PublicOnly -or
        $rsa.ToXmlString($false) -cne $publicRsa.ToXmlString($false)) {
        throw 'Signing key must be at least 2048 bits and match the client public key.'
    }
    $signature = $rsa.SignData($payloadBytes, 'SHA256')
    if (!$publicRsa.VerifyData($payloadBytes, 'SHA256', $signature)) { throw 'Manifest signature self-verification failed.' }
    $envelope = [ordered]@{ payload = [Convert]::ToBase64String($payloadBytes); signature = [Convert]::ToBase64String($signature) }
    $envelopeBytes = $utf8.GetBytes((($envelope | ConvertTo-Json -Compress) + "`n"))
    if ($envelopeBytes.Length -gt (128 * 1024)) { throw 'Signed envelope exceeds the 128 KiB client limit.' }
    $manifestPath = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifestPath)) | Out-Null
    [IO.File]::WriteAllBytes($manifestPath, $envelopeBytes)
    [IO.File]::WriteAllText((Join-Path $artifactRoot 'release-notes.txt'), ($notes + "`n"), $utf8)
    Write-Output ('Signed stable update manifest for ' + $Tag + '.')
}
finally { $rsa.Dispose(); $publicRsa.Dispose() }
