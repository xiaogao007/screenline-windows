[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$OutputDirectory = 'tests/installer-results'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)
$checks = New-Object 'System.Collections.Generic.List[string]'
$appId = '{E9282E8C-FA27-4F2B-B45A-583A93BD724E}_is1'
$uninstallKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $appId
$runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$registryView = [Microsoft.Win32.RegistryView]::Registry64
$ownedInstallation = $false
$ownedStartup = $false
$ownedStartupValue = $null
$ownedMarker = $false
$createdDataDirectory = $false
$completed = $false
$installDirectory = $null
$markerPath = $null
$expectedStartup = $null

function Assert-Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $checks.Add('PASS ' + $Message)
    Write-Output ('PASS ' + $Message)
}

function Resolve-TestPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0) {
        throw 'Paths must not be empty or contain quotes or line breaks.'
    }
    if (![System.IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $projectRoot $Path }
    return [System.IO.Path]::GetFullPath($Path)
}

function Invoke-TestProcess([string]$Path, [string[]]$Arguments, [string]$Description) {
    $process = $null
    try {
        $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -WindowStyle Hidden
        if (!$process.WaitForExit(90000)) {
            # Kill only the process this test started; never stop other Screenline instances.
            try { $process.Kill(); $process.WaitForExit(5000) | Out-Null } catch { }
            throw ($Description + ' timed out after 90 seconds.')
        }
        $exitCode = $process.ExitCode
        if ($exitCode -ne 0) { throw ($Description + ' exited with code ' + $exitCode + '.') }
    }
    finally { if ($null -ne $process) { $process.Dispose() } }
}

function Get-InstallRecord([Microsoft.Win32.RegistryView]$View) {
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $View)
    $key = $null
    try {
        $key = $baseKey.OpenSubKey($uninstallKeyPath)
        if ($null -eq $key) { return $null }
        $values = @{}
        foreach ($name in $key.GetValueNames()) { $values[$name] = $key.GetValue($name) }
        return $values
    }
    finally { if ($null -ne $key) { $key.Dispose() }; $baseKey.Dispose() }
}

function Get-StartupValue {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKeyPath)
    try {
        if ($null -eq $key -or $key.GetValueNames() -notcontains 'Screenline') { return $null }
        return [PSCustomObject]@{
            Value = $key.GetValue('Screenline', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            Kind = $key.GetValueKind('Screenline')
        }
    }
    finally { if ($null -ne $key) { $key.Dispose() } }
}

function Test-SamePath([string]$First, [string]$Second) {
    if ([string]::IsNullOrWhiteSpace($First) -or [string]::IsNullOrWhiteSpace($Second)) { return $false }
    return [string]::Equals([System.IO.Path]::GetFullPath($First).TrimEnd('\'),
        [System.IO.Path]::GetFullPath($Second).TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-Shortcut([string]$Path, [string]$Description) {
    Assert-Check (Test-Path -LiteralPath $Path -PathType Leaf) ($Description + ' exists')
    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($Path)
        Assert-Check (Test-SamePath $shortcut.TargetPath $installedExecutable) ($Description + ' targets the installed executable')
        Assert-Check (Test-SamePath $shortcut.WorkingDirectory $installDirectory) ($Description + ' uses the installation directory')
    }
    finally {
        if ($null -ne $shortcut) { [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null }
        if ($null -ne $shell) { [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    }
}

function Wait-ForUninstall {
    # Inno's first phase can exit before the temporary second phase reaches
    # usPostUninstall. Await our installation's startup cleanup as well, while
    # preserving any external portable / pre-existing startup entry.
    $waitForOwnedStartupRemoval = $ownedStartup -and
        $ownedStartupValue -ceq ('"' + $installedExecutable + '"')
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (!(Test-Path -LiteralPath $installedExecutable) -and
            !(Test-Path -LiteralPath $uninstallerPath) -and $null -eq (Get-InstallRecord $registryView)) {
            if (!$waitForOwnedStartupRemoval -or $null -eq (Get-StartupValue)) { return }
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'Uninstaller did not finish removing installed files, registration, and any owned installation startup entry within 30 seconds.'
}

$outputRoot = Resolve-TestPath $OutputDirectory
[System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$reportPath = Join-Path $outputRoot 'report.txt'
$checks.Add('Screenline Windows installer smoke test; tag=' + $Tag)

try {
    $installer = Resolve-TestPath $InstallerPath
    Assert-Check (Test-Path -LiteralPath $installer -PathType Leaf) 'Installer executable exists'
    Assert-Check ([Environment]::Is64BitOperatingSystem) 'Windows x64 is available'
    $number = '(?:0|[1-9][0-9]*)'
    $preIdentifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
    $tagPattern = '\Av(?<version>' + $number + '\.' + $number + '\.' + $number +
        '(?:-' + $preIdentifier + '(?:\.' + $preIdentifier + ')*)?' +
        '(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)\z'
    $tagMatch = [regex]::Match($Tag, $tagPattern)
    Assert-Check $tagMatch.Success 'Version tag is valid SemVer'
    $version = $tagMatch.Groups['version'].Value

    # Refuse to touch an installed copy or an existing desktop shortcut on a developer machine.
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        Assert-Check ($null -eq (Get-InstallRecord $view)) ('No existing Screenline installation in HKCU ' + $view)
    }
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    Assert-Check (![string]::IsNullOrWhiteSpace($desktop) -and ![string]::IsNullOrWhiteSpace($programs)) 'Current-user shortcut folders are available'
    $desktopShortcut = Join-Path $desktop 'Screenline.lnk'
    Assert-Check (!(Test-Path -LiteralPath $desktopShortcut)) 'No existing Screenline desktop shortcut will be overwritten'
    $priorStartup = Get-StartupValue

    $testId = [Guid]::NewGuid().ToString('N')
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $installDirectory = Join-Path $temporaryRoot ('Screenline installer check-' + $testId)
    Assert-Check ($installDirectory.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        !(Test-Path -LiteralPath $installDirectory)) 'Installation uses a new isolated temporary directory with spaces'
    $groupName = 'Screenline installer check-' + $testId
    $startMenuDirectory = Join-Path $programs $groupName
    Assert-Check (!(Test-Path -LiteralPath $startMenuDirectory)) 'Start menu uses a new isolated group'
    $startMenuShortcut = Join-Path $startMenuDirectory 'Screenline.lnk'
    $installedExecutable = Join-Path $installDirectory 'Screenline.exe'
    $uninstallerPath = Join-Path $installDirectory 'unins000.exe'
    $expectedStartup = '"' + $installedExecutable + '"'

    $installArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        ('/DIR="' + $installDirectory + '"'), ('/GROUP="' + $groupName + '"'), '/TASKS=desktopicon',
        ('/LOG="' + (Join-Path $outputRoot 'install.log') + '"'))
    # Ownership is set before launch so a partially failed setup is also cleaned up.
    $ownedInstallation = $true
    Invoke-TestProcess $installer $installArguments 'Silent installation'
    foreach ($fileName in @('Screenline.exe', 'Screenline.exe.config', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.txt')) {
        Assert-Check (Test-Path -LiteralPath (Join-Path $installDirectory $fileName) -PathType Leaf) ('Installed payload contains ' + $fileName)
    }
    Assert-Check ((Get-Content -LiteralPath (Join-Path $installDirectory 'LICENSE') -Raw) -match 'MIT License') 'Installed license can be read'
    Assert-Check (Test-Path -LiteralPath $uninstallerPath -PathType Leaf) 'Windows uninstaller exists'
    Assert-Shortcut $desktopShortcut 'Desktop shortcut'
    Assert-Shortcut $startMenuShortcut 'Start menu shortcut'

    $record = Get-InstallRecord $registryView
    Assert-Check ($null -ne $record) 'Current-user Windows uninstall registration exists'
    Assert-Check ([string]$record['DisplayName'] -match '(?i)Screenline') 'Windows uninstall registration has the application name'
    Assert-Check ([string]$record['Publisher'] -ceq 'xiaogao007') 'Windows uninstall registration has the publisher'
    Assert-Check ([string]$record['DisplayVersion'] -ceq $version) 'Windows uninstall registration matches the version tag'
    Assert-Check (Test-SamePath ([string]$record['InstallLocation']) $installDirectory) 'Windows uninstall registration identifies the installation directory'
    foreach ($valueName in @('UninstallString', 'QuietUninstallString')) {
        Assert-Check (([string]$record[$valueName]).StartsWith('"' + $uninstallerPath + '"', [System.StringComparison]::OrdinalIgnoreCase)) ('Windows uninstall registration includes ' + $valueName)
    }

    $selfTestDirectory = Join-Path $outputRoot ('installed-self-test-' + $testId)
    Invoke-TestProcess $installedExecutable @('--self-test', ('"' + $selfTestDirectory + '"')) 'Installed application self-test'
    $selfTestReportPath = Join-Path $selfTestDirectory 'report.txt'
    Assert-Check (Test-Path -LiteralPath $selfTestReportPath -PathType Leaf) 'Installed executable wrote an isolated self-test report'
    $selfTestReport = @(Get-Content -LiteralPath $selfTestReportPath -Encoding UTF8)
    $results = @($selfTestReport | Where-Object { $_ -match '^RESULT:' })
    $passes = @($selfTestReport | Where-Object { $_ -match '^PASS\s+\S' }).Count
    $failures = @($selfTestReport | Where-Object { $_ -match '^FAIL\b' }).Count
    Assert-Check ($results.Count -eq 1 -and $results[0] -ceq 'RESULT: PASS; failures=0' -and
        $passes -ge 35 -and $failures -eq 0) ('Installed executable passes all native self-tests (' + $passes + ' checks)')

    # Reinstall over the same AppId and directory to exercise repair / future upgrade behavior.
    $repairArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        ('/DIR="' + $installDirectory + '"'), ('/GROUP="' + $groupName + '"'), '/TASKS=desktopicon',
        ('/LOG="' + (Join-Path $outputRoot 'repair.log') + '"'))
    Invoke-TestProcess $installer $repairArguments 'Silent repair installation'
    $repairedRecord = Get-InstallRecord $registryView
    Assert-Check ($null -ne $repairedRecord -and
        (Test-SamePath ([string]$repairedRecord['InstallLocation']) $installDirectory) -and
        [string]$repairedRecord['DisplayVersion'] -ceq $version) 'Repair reuses the existing Windows uninstall registration'
    Assert-Check (@(Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*.exe').Count -eq 1) 'Repair keeps a single uninstaller'
    Assert-Shortcut $desktopShortcut 'Repaired desktop shortcut'
    Assert-Shortcut $startMenuShortcut 'Repaired start menu shortcut'

    $afterInstallStartup = Get-StartupValue
    if ($null -eq $priorStartup) {
        Assert-Check ($null -eq $afterInstallStartup) 'Installation leaves Windows startup disabled by default'
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKeyPath)
        try {
            if ($key.GetValueNames() -contains 'Screenline') { throw 'A startup entry appeared during testing; refusing to overwrite it.' }
            $ownedStartupValue = $expectedStartup
            $ownedStartup = $true
            $key.SetValue('Screenline', $ownedStartupValue, [Microsoft.Win32.RegistryValueKind]::String)
        }
        finally { $key.Dispose() }
        Assert-Check ((Get-StartupValue).Value -ceq $expectedStartup) 'Test startup entry points to the installed executable'
    }
    else {
        Assert-Check ($null -ne $afterInstallStartup -and $afterInstallStartup.Value -ceq $priorStartup.Value -and
            $afterInstallStartup.Kind -eq $priorStartup.Kind) 'Installation preserves the existing Windows startup entry'
    }

    # Only create a uniquely named marker; never enumerate or change the user's picture library.
    $localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $dataDirectory = Join-Path $localData 'Screenline'
    $createdDataDirectory = !(Test-Path -LiteralPath $dataDirectory)
    [System.IO.Directory]::CreateDirectory($dataDirectory) | Out-Null
    $markerPath = Join-Path $dataDirectory ('.installer-smoke-' + $testId + '.txt')
    $markerValue = 'Screenline installer preservation fixture ' + $testId
    [System.IO.File]::WriteAllText($markerPath, $markerValue, $utf8)
    $ownedMarker = $true

    Invoke-TestProcess $uninstallerPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        ('/LOG="' + (Join-Path $outputRoot 'uninstall.log') + '"')) 'Silent uninstall'
    Wait-ForUninstall
    foreach ($fileName in @('Screenline.exe', 'Screenline.exe.config', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.txt', 'unins000.exe')) {
        Assert-Check (!(Test-Path -LiteralPath (Join-Path $installDirectory $fileName))) ('Uninstall removes ' + $fileName)
    }
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        Assert-Check ($null -eq (Get-InstallRecord $view)) ('Uninstall removes the HKCU ' + $view + ' registration')
    }
    Assert-Check (!(Test-Path -LiteralPath $desktopShortcut)) 'Uninstall removes the desktop shortcut'
    Assert-Check (!(Test-Path -LiteralPath $startMenuShortcut)) 'Uninstall removes the start menu shortcut'
    if ($ownedStartup) {
        Assert-Check ($null -eq (Get-StartupValue)) 'Uninstall removes startup entry belonging to the installed executable'
        $ownedStartup = $false
    }
    else {
        $afterUninstallStartup = Get-StartupValue
        Assert-Check ($null -ne $afterUninstallStartup -and $afterUninstallStartup.Value -ceq $priorStartup.Value -and
            $afterUninstallStartup.Kind -eq $priorStartup.Kind) 'Uninstall preserves the previous Windows startup entry'
    }
    Assert-Check ((Test-Path -LiteralPath $markerPath -PathType Leaf) -and
        [System.IO.File]::ReadAllText($markerPath) -ceq $markerValue) 'Uninstall preserves local application data'
    $ownedInstallation = $false

    # A fresh install with the task explicitly deselected exercises the optional shortcut.
    # Inno Setup documents /TASKS="!desktopicon" as explicit task deselection:
    # https://jrsoftware.org/ishelp/topic_setupcmdline.htm
    if ($null -eq $priorStartup) {
        $portableDirectory = Join-Path $temporaryRoot ('Screenline portable check-' + $testId)
        $portableStartup = '"' + (Join-Path $portableDirectory 'PortableScreenline.exe') + '"'
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKeyPath)
        try {
            if ($key.GetValueNames() -contains 'Screenline') { throw 'A startup entry appeared during testing; refusing to overwrite it.' }
            $ownedStartupValue = $portableStartup
            $ownedStartup = $true
            $key.SetValue('Screenline', $ownedStartupValue, [Microsoft.Win32.RegistryValueKind]::String)
        }
        finally { $key.Dispose() }
        $preservedStartup = Get-StartupValue
        Assert-Check ($null -ne $preservedStartup -and $preservedStartup.Value -ceq $portableStartup) 'Portable startup preservation fixture uses an external executable path'
    }
    else { $preservedStartup = $priorStartup }

    $installDirectory = Join-Path $temporaryRoot ('Screenline installer no desktop-' + $testId)
    Assert-Check ($installDirectory.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        !(Test-Path -LiteralPath $installDirectory)) 'Second installation uses a new isolated temporary directory'
    $groupName = 'Screenline installer no desktop-' + $testId
    $startMenuDirectory = Join-Path $programs $groupName
    Assert-Check (!(Test-Path -LiteralPath $startMenuDirectory)) 'Second installation uses a new isolated Start menu group'
    $startMenuShortcut = Join-Path $startMenuDirectory 'Screenline.lnk'
    $installedExecutable = Join-Path $installDirectory 'Screenline.exe'
    $uninstallerPath = Join-Path $installDirectory 'unins000.exe'
    $noDesktopArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        ('/DIR="' + $installDirectory + '"'), ('/GROUP="' + $groupName + '"'), '/TASKS="!desktopicon"',
        ('/LOG="' + (Join-Path $outputRoot 'install-no-desktop.log') + '"'))
    $ownedInstallation = $true
    Invoke-TestProcess $installer $noDesktopArguments 'Silent installation without desktop shortcut'
    Assert-Check (Test-Path -LiteralPath $installedExecutable -PathType Leaf) 'Second installation contains the application executable'
    Assert-Check (!(Test-Path -LiteralPath $desktopShortcut)) 'Deselected desktop shortcut task creates no desktop shortcut'
    Assert-Shortcut $startMenuShortcut 'Start menu shortcut without desktop task'
    $secondRecord = Get-InstallRecord $registryView
    Assert-Check ($null -ne $secondRecord -and
        (Test-SamePath ([string]$secondRecord['InstallLocation']) $installDirectory)) 'Second installation registers the new installation directory'
    $afterSecondInstallStartup = Get-StartupValue
    Assert-Check ($null -ne $afterSecondInstallStartup -and
        $afterSecondInstallStartup.Value -ceq $preservedStartup.Value -and
        $afterSecondInstallStartup.Kind -eq $preservedStartup.Kind) 'Second installation preserves the external Windows startup entry'

    Invoke-TestProcess $uninstallerPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        ('/LOG="' + (Join-Path $outputRoot 'uninstall-no-desktop.log') + '"')) 'Silent uninstall without desktop shortcut'
    Wait-ForUninstall
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        Assert-Check ($null -eq (Get-InstallRecord $view)) ('Second uninstall removes the HKCU ' + $view + ' registration')
    }
    Assert-Check (!(Test-Path -LiteralPath $desktopShortcut)) 'Second uninstall leaves the desktop without an application shortcut'
    Assert-Check (!(Test-Path -LiteralPath $startMenuShortcut)) 'Second uninstall removes its Start menu shortcut'
    $afterSecondUninstallStartup = Get-StartupValue
    Assert-Check ($null -ne $afterSecondUninstallStartup -and
        $afterSecondUninstallStartup.Value -ceq $preservedStartup.Value -and
        $afterSecondUninstallStartup.Kind -eq $preservedStartup.Kind) 'Uninstall preserves the startup entry for an external portable copy'
    Assert-Check ((Test-Path -LiteralPath $markerPath -PathType Leaf) -and
        [System.IO.File]::ReadAllText($markerPath) -ceq $markerValue) 'Second uninstall also preserves local application data'
    $ownedInstallation = $false
    $completed = $true
}
catch {
    $checks.Add('FAIL ' + $_.Exception.Message)
    throw
}
finally {
    if ($ownedInstallation -and $null -ne $installDirectory) {
        try {
            $cleanupUninstaller = Join-Path $installDirectory 'unins000.exe'
            if (Test-Path -LiteralPath $cleanupUninstaller -PathType Leaf) {
                $cleanupRecord = Get-InstallRecord $registryView
                if ($null -eq $cleanupRecord -or (Test-SamePath ([string]$cleanupRecord['InstallLocation']) $installDirectory)) {
                    Invoke-TestProcess $cleanupUninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
                        ('/LOG="' + (Join-Path $outputRoot 'cleanup-uninstall.log') + '"')) 'Failure cleanup uninstall'
                }
            }
        }
        catch { $checks.Add('CLEANUP WARNING ' + $_.Exception.Message) }
    }
    if ($ownedStartup) {
        try {
            $currentStartup = Get-StartupValue
            if ($null -ne $currentStartup -and $currentStartup.Value -ceq $ownedStartupValue) {
                $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKeyPath, $true)
                try { $key.DeleteValue('Screenline', $false) } finally { $key.Dispose() }
            }
        }
        catch { $checks.Add('CLEANUP WARNING ' + $_.Exception.Message) }
    }
    if ($ownedMarker) {
        try {
            if ((Test-Path -LiteralPath $markerPath -PathType Leaf) -and
                [System.IO.File]::ReadAllText($markerPath) -ceq $markerValue) {
                Remove-Item -LiteralPath $markerPath
            }
            if ($createdDataDirectory -and (Test-Path -LiteralPath $dataDirectory -PathType Container) -and
                [System.IO.Directory]::GetFileSystemEntries($dataDirectory).Length -eq 0) {
                [System.IO.Directory]::Delete($dataDirectory, $false)
            }
        }
        catch { $checks.Add('CLEANUP WARNING ' + $_.Exception.Message) }
    }
    $passedCheckCount = @($checks | Where-Object { $_ -match '^PASS\s' }).Count
    $checks.Add('CHECKS: passed=' + $passedCheckCount)
    $checks.Add(('RESULT: ' + $(if ($completed) { 'PASS' } else { 'FAIL' })))
    [System.IO.File]::WriteAllLines($reportPath, $checks.ToArray(), $utf8)
    Write-Output ('Installer smoke report: ' + $reportPath)
}
