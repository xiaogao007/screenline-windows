[CmdletBinding()]
param([string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'update-source.json'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$baseUrl = [string]$env:SCREENLINE_UPDATE_BASE_URL
$publicKey = [string]$env:SCREENLINE_UPDATE_PUBLIC_KEY
if ([string]::IsNullOrWhiteSpace($baseUrl) -ne [string]::IsNullOrWhiteSpace($publicKey)) {
    throw 'Set both SCREENLINE_UPDATE_BASE_URL and SCREENLINE_UPDATE_PUBLIC_KEY, or leave both empty.'
}
if (![string]::IsNullOrWhiteSpace($baseUrl)) {
    $uri = $null
    if (![Uri]::TryCreate($baseUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -cne 'https' -or
        !$uri.IsDefaultPort -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $uri.HostNameType -ne [UriHostNameType]::Dns -or !$baseUrl.EndsWith('/') -or
        $uri.AbsolutePath -notmatch '\A/(?:[A-Za-z0-9_-]+/)*\z') {
        throw 'Update BaseUrl must be an HTTPS DNS URL ending in /, with a simple object prefix and no credentials, port, query or fragment.'
    }
    if ($publicKey.Length -gt 16384) { throw 'Update public key is too large.' }
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create((New-Object System.IO.StringReader($publicKey)), $settings)
    $document = New-Object System.Xml.XmlDocument
    $document.XmlResolver = $null
    try { $document.Load($reader) } finally { $reader.Dispose() }
    if ($document.DocumentElement.Name -cne 'RSAKeyValue' -or
        $document.DocumentElement.ChildNodes.Count -ne 2 -or
        $document.DocumentElement.SelectNodes('Modulus').Count -ne 1 -or
        $document.DocumentElement.SelectNodes('Exponent').Count -ne 1) {
        throw 'SCREENLINE_UPDATE_PUBLIC_KEY must contain only an RSA public key XML (Modulus and Exponent). Never place the private key in client configuration.'
    }
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    $rsa.PersistKeyInCsp = $false
    try {
        $rsa.FromXmlString($publicKey)
        if ($rsa.KeySize -lt 2048 -or $rsa.KeySize -gt 8192) { throw 'Use an RSA public key between 2048 and 8192 bits.' }
        $publicKey = $rsa.ToXmlString($false)
    }
    finally { $rsa.Dispose() }
} else {
    $baseUrl = ''
    $publicKey = ''
}
$configuration = [ordered]@{ BaseUrl = $baseUrl; PublicKeyXml = $publicKey }
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullOutputPath)) | Out-Null
[IO.File]::WriteAllText($fullOutputPath, (($configuration | ConvertTo-Json -Compress) + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Output $(if ($baseUrl) { 'Domestic update source configuration generated.' } else { 'Domestic update source is disabled; GitHub remains available.' })
