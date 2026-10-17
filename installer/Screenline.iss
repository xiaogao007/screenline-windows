; Compile through scripts/build-installer.ps1 so version and payload validation run first.
#if Ver < 0x06070000
  #error Inno Setup 6.7 or later is required.
#endif
#ifndef ReleaseTag
  #error ReleaseTag is required, for example /DReleaseTag=v0.1.3
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion is required, for example /DReleaseVersion=0.1.3
#endif
#ifndef FileVersion
  #error FileVersion is required, for example /DFileVersion=0.1.3.0
#endif
#ifndef SourceRoot
  #define SourceRoot AddBackslash(SourcePath) + ".."
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir AddBackslash(SourceRoot) + "dist"
#endif

[Setup]
AppId={{E9282E8C-FA27-4F2B-B45A-583A93BD724E}
AppName=Screenline
AppVersion={#ReleaseVersion}
AppVerName=Screenline {#ReleaseVersion}
AppPublisher=xiaogao007
AppPublisherURL=https://github.com/xiaogao007/screenline-windows
AppSupportURL=https://github.com/xiaogao007/screenline-windows/issues
AppUpdatesURL=https://github.com/xiaogao007/screenline-windows/releases
DefaultDirName={localappdata}\Programs\Screenline
DefaultGroupName=Screenline
DisableProgramGroupPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
UsePreviousAppDir=yes
UsePreviousTasks=yes
Uninstallable=yes
UninstallDisplayName=Screenline
UninstallDisplayIcon={app}\Screenline.exe
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Screenline-{#ReleaseTag}-windows-x64-setup
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoProductTextVersion={#ReleaseVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=Screenline.exe
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
english.DesktopShortcut=Create a &desktop shortcut
english.StartMenuShortcut=Screenline
english.UninstallShortcut=Uninstall Screenline
english.LaunchScreenline=Launch Screenline
english.DotNetRequired=Screenline requires .NET Framework 4.8 or later. Install it from Microsoft, then run this installer again.
chinesesimplified.DesktopShortcut=创建桌面快捷方式(&D)
chinesesimplified.StartMenuShortcut=Screenline
chinesesimplified.UninstallShortcut=Uninstall Screenline
chinesesimplified.LaunchScreenline=启动截图晾台 Screenline
chinesesimplified.DotNetRequired=截图晾台需要 .NET Framework 4.8 或更新版本。请先从微软安装此运行时，再重新运行安装程序。

[Tasks]
; Checked by default on first install; later upgrades keep the user's choice.
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Keep this allowlist aligned with the portable package. Never include build or user data.
Source: "{#SourceRoot}\Screenline.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\Screenline.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\update-source.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\docs\WINDOWS.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "{#SourceRoot}\docs\MIGRATION.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "{#SourceRoot}\docs\images\shelf-preview.png"; DestDir: "{app}\docs\images"; Flags: ignoreversion
Source: "{#SourceRoot}\docs\images\shelf-empty.png"; DestDir: "{app}\docs\images"; Flags: ignoreversion

[Icons]
Name: "{group}\{cm:StartMenuShortcut}"; Filename: "{app}\Screenline.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Screenline"; Filename: "{app}\Screenline.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Screenline.exe"; Description: "{cm:LaunchScreenline}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(CustomMessage('DotNetRequired'), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
  InstalledExecutable: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    InstalledExecutable := ExpandConstant('{app}\Screenline.exe');
    if RegQueryStringValue(HKCU64,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'Screenline', StartupCommand) then
    begin
      // A portable copy may have replaced the startup entry. Only remove our own path.
      if CompareText(Trim(StartupCommand), '"' + InstalledExecutable + '"') = 0 then
        RegDeleteValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Screenline');
    end;
    // %LOCALAPPDATA%\Screenline holds user screenshots and preferences. Keep it intact.
  end;
end;
