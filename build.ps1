param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (!(Test-Path -LiteralPath $compilerPath)) { throw 'Windows .NET Framework 4.8 is required.' }
$references = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $frameworkPath 'WPF') $_) }
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/codepage:65001',('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')),('/out:' + (Join-Path $projectRoot 'Screenline.exe')))
& $compilerPath @arguments @references @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output ('Built ' + (Join-Path $projectRoot 'Screenline.exe'))
if ($Test) {
    $testOutputPath = Join-Path $projectRoot 'tests\results'
    $process = Start-Process -FilePath (Join-Path $projectRoot 'Screenline.exe') -ArgumentList @('--self-test',('"' + $testOutputPath + '"')) -Wait -PassThru -WindowStyle Hidden
    Get-Content -LiteralPath (Join-Path $testOutputPath 'report.txt')
    if ($process.ExitCode -ne 0) { throw 'Self tests failed.' }
}
