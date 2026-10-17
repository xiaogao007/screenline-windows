[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('Screenline update publishing ' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$savedBase = $env:SCREENLINE_UPDATE_BASE_URL
$savedPublic = $env:SCREENLINE_UPDATE_PUBLIC_KEY
$savedPrivate = $env:UPDATE_SIGNING_PRIVATE_KEY_XML
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider(2048)
$rsa.PersistKeyInCsp = $false
$passes = 0
function Assert-Check([bool]$Condition, [string]$Name) {
    if (!$Condition) { throw ('FAIL ' + $Name) }
    $script:passes++
    Write-Output ('PASS ' + $Name)
}
function Assert-Reject([scriptblock]$Action, [string]$Name, [string]$ErrorPattern = '') {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = !$ErrorPattern -or $_.Exception.Message -like $ErrorPattern }
    Assert-Check $rejected $Name
}
try {
    $configPath = Join-Path $testRoot 'update-source.json'
    $env:SCREENLINE_UPDATE_BASE_URL = ''
    $env:SCREENLINE_UPDATE_PUBLIC_KEY = ''
    & (Join-Path $PSScriptRoot 'write-update-source.ps1') -OutputPath $configPath | Out-Null
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    Assert-Check ($config.BaseUrl -ceq '' -and $config.PublicKeyXml -ceq '') 'Default configuration leaves domestic source disabled'
    $env:SCREENLINE_UPDATE_BASE_URL = 'https://example-bucket.oss-cn-hangzhou.aliyuncs.com/screenline/'
    Assert-Reject { & (Join-Path $PSScriptRoot 'write-update-source.ps1') -OutputPath $configPath } 'Half configured source is rejected'
    $env:SCREENLINE_UPDATE_PUBLIC_KEY = $rsa.ToXmlString($false)
    & (Join-Path $PSScriptRoot 'write-update-source.ps1') -OutputPath $configPath | Out-Null
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    Assert-Check ($config.BaseUrl -ceq $env:SCREENLINE_UPDATE_BASE_URL -and $config.PublicKeyXml -ceq $rsa.ToXmlString($false)) 'Public configuration preserves URL and matching RSA public key'
    Assert-Check (!(Get-Content -LiteralPath $configPath -Raw).Contains('<D>')) 'Generated client configuration contains no private key'
    $env:SCREENLINE_UPDATE_PUBLIC_KEY = $rsa.ToXmlString($true)
    Assert-Reject { & (Join-Path $PSScriptRoot 'write-update-source.ps1') -OutputPath $configPath } 'Private key in client configuration is rejected'
    $env:SCREENLINE_UPDATE_PUBLIC_KEY = $rsa.ToXmlString($false)
    $env:SCREENLINE_UPDATE_BASE_URL = 'http://example.invalid/screenline/'
    Assert-Reject { & (Join-Path $PSScriptRoot 'write-update-source.ps1') -OutputPath $configPath } 'HTTP configuration is rejected'
    $env:SCREENLINE_UPDATE_BASE_URL = 'https://example.invalid/screenline/?token=secret'
    Assert-Reject { & (Join-Path $PSScriptRoot 'write-update-source.ps1') -OutputPath $configPath } 'URL query credentials are rejected'
    $env:SCREENLINE_UPDATE_BASE_URL = 'https://example.invalid/screenline/'
    $env:UPDATE_SIGNING_PRIVATE_KEY_XML = $rsa.ToXmlString($true)
    $installerName = 'Screenline-v0.1.7-windows-x64-setup.exe'
    $installerPath = Join-Path $testRoot $installerName
    [IO.File]::WriteAllBytes($installerPath, [byte[]](1, 2, 3, 4, 5))
    $hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $testRoot 'SHA256SUMS.txt'), ($hash + '  ' + $installerName + "`n"))
    [IO.File]::WriteAllText((Join-Path $testRoot 'release-notes.md'), '# Update notes', (New-Object Text.UTF8Encoding($false)))
    $manifestPath = Join-Path $testRoot 'latest.json'
    & (Join-Path $PSScriptRoot 'new-update-manifest.ps1') -Tag v0.1.7 -ArtifactDirectory $testRoot -OutputPath $manifestPath | Out-Null
    $envelope = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $payloadBytes = [Convert]::FromBase64String($envelope.payload)
    $signature = [Convert]::FromBase64String($envelope.signature)
    Assert-Check ($rsa.VerifyData($payloadBytes, 'SHA256', $signature)) 'Manifest signature verifies against exact payload bytes'
    $payload = [Text.Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json
    Assert-Check ($payload.schema -eq 1 -and $payload.tag -ceq 'v0.1.7' -and $payload.installer -ceq $installerName -and $payload.size -eq 5 -and $payload.sha256 -ceq $hash) 'Signed payload matches installer size, SHA-256 and version'
    $payloadBytes[0] = $payloadBytes[0] -bxor 1
    Assert-Check (!$rsa.VerifyData($payloadBytes, 'SHA256', $signature)) 'Tampered payload fails signature verification'
    Assert-Reject { & (Join-Path $PSScriptRoot 'new-update-manifest.ps1') -Tag v0.1.8-rc.1 -ArtifactDirectory $testRoot -OutputPath $manifestPath } 'Prerelease cannot update the stable domestic channel'
    [IO.File]::WriteAllText((Join-Path $testRoot 'release-notes.md'), (([string][char]1) * 16384), (New-Object Text.UTF8Encoding($false)))
    Assert-Reject { & (Join-Path $PSScriptRoot 'new-update-manifest.ps1') -Tag v0.1.7 -ArtifactDirectory $testRoot -OutputPath $manifestPath } 'Unsupported control characters in release notes are rejected' '*unsupported control*'
    [IO.File]::WriteAllText((Join-Path $testRoot 'release-notes.md'), ('x' + (([string][char]0x2028) * 16000) + 'x'), (New-Object Text.UTF8Encoding($false)))
    Assert-Reject { & (Join-Path $PSScriptRoot 'new-update-manifest.ps1') -Tag v0.1.7 -ArtifactDirectory $testRoot -OutputPath $manifestPath } 'Escaped notes exceeding the client payload byte limit are rejected' '*64 KiB client limit*'
    [IO.File]::WriteAllBytes($installerPath, [byte[]](6, 7, 8, 9))
    Assert-Reject { & (Join-Path $PSScriptRoot 'new-update-manifest.ps1') -Tag v0.1.7 -ArtifactDirectory $testRoot -OutputPath $manifestPath } 'Changed installer fails checksum before signing'
    Write-Output ('RESULT: PASS; checks=' + $passes)
}
finally {
    $env:SCREENLINE_UPDATE_BASE_URL = $savedBase
    $env:SCREENLINE_UPDATE_PUBLIC_KEY = $savedPublic
    $env:UPDATE_SIGNING_PRIVATE_KEY_XML = $savedPrivate
    $rsa.Dispose()
    # Delete only named temporary fixtures created by this test; never traverse user data.
    foreach ($name in @('update-source.json', 'Screenline-v0.1.7-windows-x64-setup.exe', 'SHA256SUMS.txt', 'release-notes.md', 'release-notes.txt', 'latest.json')) {
        $path = Join-Path $testRoot $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { [IO.File]::Delete($path) }
    }
    if ([IO.Directory]::GetFileSystemEntries($testRoot).Length -eq 0) { [IO.Directory]::Delete($testRoot) }
}
