#define AppName "codexU"
#define AppPublisher "codexU Windows contributors"
#define AppVersion GetEnv("CODEXU_VERSION")
#define NumericVersion GetEnv("CODEXU_NUMERIC_VERSION")
#define PublishDirectory GetEnv("CODEXU_PUBLISH_DIR")
#define OutputDirectory GetEnv("CODEXU_INSTALLER_OUTPUT_DIR")

#if AppVersion == ""
  #error "CODEXU_VERSION is required"
#endif
#if NumericVersion == ""
  #error "CODEXU_NUMERIC_VERSION is required"
#endif
#if PublishDirectory == ""
  #error "CODEXU_PUBLISH_DIR is required"
#endif
#if OutputDirectory == ""
  #error "CODEXU_INSTALLER_OUTPUT_DIR is required"
#endif

[Setup]
AppId={{A4B05572-70A1-4A5C-A9CE-08FA966F4E8E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/yuweiyang9611/CodexHelperProject
AppSupportURL=https://github.com/yuweiyang9611/CodexHelperProject/issues
AppUpdatesURL=https://github.com/yuweiyang9611/CodexHelperProject/releases
DefaultDirName={localappdata}\Programs\codexU
DefaultGroupName=codexU
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDirectory}
OutputBaseFilename=CodexU-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\CodexU.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\CodexU.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter=CodexU.exe,CodexU.Sidecar.exe,CodexU.App.exe
RestartApplications=no
MinVersion=10.0.19045
VersionInfoVersion={#NumericVersion}.0
VersionInfoProductName=codexU
VersionInfoProductVersion={#NumericVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Remove files and directories owned by the former WPF/WebView2 distribution.
; The Electron payload is copied afterwards, while unrelated files and Inno's
; own uninstall data remain untouched. Every entry is gated by a cached
; positive identification of the legacy WPF application directory.
Type: files; Name: "{app}\CodexU.App.*"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\CodexU.Contracts.*"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\CodexU.Core.*"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\CodexU.Infrastructure.*"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\Microsoft.*.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\Microsoft.*.xml"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\SQLitePCLRaw.*"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\System*.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\Presentation*.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\UIAutomation*.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\Accessibility.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\clr*.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\coreclr.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\createdump.exe"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\D3DCompiler_47_cor3.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\DirectWriteForwarder.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\e_sqlite3.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\hostfxr.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\hostpolicy.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\mscor*.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\msquic.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\netstandard.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\PenImc_cor3.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\ReachFramework.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\vcruntime140_cor3.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\WebView2Loader.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\WindowsBase.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\WindowsFormsIntegration.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\wpfgfx_cor3.dll"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\THIRD-PARTY-INVENTORY.md"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\THIRD-PARTY-LICENSES.txt"; Check: ShouldRemoveLegacyWpfFiles
Type: files; Name: "{app}\THIRD-PARTY-NOTICES.md"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\cs"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\de"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\es"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\fr"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\it"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\ja"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\ko"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\pl"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\pt-BR"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\ru"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\tr"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\zh-Hans"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\zh-Hant"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\LICENSES"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\runtimes"; Check: ShouldRemoveLegacyWpfFiles
Type: filesandordirs; Name: "{app}\web"; Check: ShouldRemoveLegacyWpfFiles

[Icons]
Name: "{group}\codexU"; Filename: "{app}\CodexU.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 codexU"; Filename: "{uninstallexe}"
Name: "{autodesktop}\codexU"; Filename: "{app}\CodexU.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; Clear startup state during installation only when the selected directory or
; Run command positively identifies the former WPF host. This preserves a valid
; Electron login item across silent reinstalls. CurUninstallStepChanged performs
; the unconditional uninstall cleanup even when these checked entries are skipped.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "codexU"; Flags: deletevalue uninsdeletevalue; Check: ShouldRemoveLegacyStartupRegistration
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "codexU"; Flags: deletevalue uninsdeletevalue; Check: ShouldRemoveLegacyStartupRegistration

[Run]
Filename: "{app}\CodexU.exe"; Description: "启动 codexU"; Flags: nowait postinstall skipifsilent

[Code]
var
  LegacyWpfInstallDetected: Boolean;
  LegacyStartupRegistrationDetected: Boolean;

function IsLegacyWpfStartupCommand(): Boolean;
var
  StartupCommand: String;
begin
  Result := False;
  if not RegQueryStringValue(
    HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Run',
    'codexU',
    StartupCommand) then
    exit;

  StartupCommand := Trim(StartupCommand);
  if Length(StartupCommand) >= 2 then
  begin
    if (StartupCommand[1] = '"') and
       (StartupCommand[Length(StartupCommand)] = '"') then
      StartupCommand := Copy(StartupCommand, 2, Length(StartupCommand) - 2);
  end;

  Result := CompareText(ExtractFileName(StartupCommand), 'CodexU.App.exe') = 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  LegacyWpfInstallDetected :=
    FileExists(ExpandConstant('{app}\CodexU.App.exe')) and
    FileExists(ExpandConstant('{app}\CodexU.App.dll'));
  LegacyStartupRegistrationDetected :=
    LegacyWpfInstallDetected or IsLegacyWpfStartupCommand();
  Result := '';
end;

function ShouldRemoveLegacyWpfFiles(): Boolean;
begin
  Result := LegacyWpfInstallDetected;
end;

function ShouldRemoveLegacyStartupRegistration(): Boolean;
begin
  Result := LegacyStartupRegistrationDetected;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  ShutdownParameters: String;
begin
  Result := True;
  if not FileExists(ExpandConstant('{app}\CodexU.exe')) then
    exit;

  ShutdownParameters :=
    '--maintenance-shutdown --maintenance-shutdown-marker="' +
    ExpandConstant('{tmp}\codexu-maintenance-shutdown.marker') + '"';
  ResultCode := -1;
  Result :=
    Exec(
      ExpandConstant('{app}\CodexU.exe'),
      ShutdownParameters,
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
  if not Result then
    SuppressibleMsgBox(
      'codexU 仍在运行，无法安全卸载。请关闭应用后重试。',
      mbError,
      MB_OK,
      IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'codexU');
    RegDeleteValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run',
      'codexU');
  end;
end;
