[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PrivateKeyPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (![IO.Path]::IsPathRooted($PrivateKeyPath)) { throw 'Provide an absolute private key path outside this repository.' }
$keyPath = [IO.Path]::GetFullPath($PrivateKeyPath)
if ($keyPath.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Private signing keys must be stored outside the repository.' }
if (Test-Path -LiteralPath $keyPath) { throw 'Refusing to overwrite an existing key.' }
$directory = [IO.Path]::GetDirectoryName($keyPath)
if (!(Test-Path -LiteralPath $directory -PathType Container)) { throw 'Create the private key directory with suitable access permissions first.' }
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp = $false
$stream = $null
try {
    # CreateNew prevents replacement even when another process created the path after validation.
    $stream = New-Object IO.FileStream($keyPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($rsa.ToXmlString($true))
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
    $stream.Dispose()
    $stream = $null
    if ($env:OS -eq 'Windows_NT') {
        $acl = New-Object Security.AccessControl.FileSecurity
        $acl.SetOwner([Security.Principal.WindowsIdentity]::GetCurrent().User)
        $acl.SetAccessRuleProtection($true, $false)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.WindowsIdentity]::GetCurrent().User, 'FullControl', 'Allow')
        $acl.AddAccessRule($rule)
        Set-Acl -LiteralPath $keyPath -AclObject $acl
    }
    else { & chmod 600 -- $keyPath; if ($LASTEXITCODE -ne 0) { throw 'Unable to restrict private key permissions.' } }
    # Only the public key is returned. Copy the private file directly into a GitHub secret without logging it.
    Write-Output $rsa.ToXmlString($false)
}
finally {
    if ($null -ne $stream) { $stream.Dispose() }
    $rsa.Dispose()
}
