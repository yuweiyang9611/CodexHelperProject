#define AppName "codexU"
#define AppPublisher "codexU Windows contributors"
#define AppVersion GetEnv("CODEXU_VERSION")
#define NumericVersion GetEnv("CODEXU_NUMERIC_VERSION")
#define PublishDirectory GetEnv("CODEXU_PUBLISH_DIR")
#define OutputDirectory GetEnv("CODEXU_INSTALLER_OUTPUT_DIR")
#define WebView2RuntimeDownloadUrl "https://developer.microsoft.com/microsoft-edge/webview2/#download-section"

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
UninstallDisplayIcon={app}\CodexU.App.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.19045
VersionInfoVersion={#NumericVersion}.0
VersionInfoProductName=codexU
VersionInfoProductVersion={#NumericVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked
Name: "startup"; Description: "登录 Windows 后自动启动 codexU"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\codexU"; Filename: "{app}\CodexU.App.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 codexU"; Filename: "{uninstallexe}"
Name: "{autodesktop}\codexU"; Filename: "{app}\CodexU.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "codexU"; ValueData: """{app}\CodexU.App.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\CodexU.App.exe"; Description: "启动 codexU"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2Machine64Key = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Machine32Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2UserKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function IsUsableWebView2Version(Version: String): Boolean;
var
  Index: Integer;
  HasDigit: Boolean;
  HasNonZeroDigit: Boolean;
begin
  Version := Trim(Version);
  HasDigit := False;
  HasNonZeroDigit := False;

  for Index := 1 to Length(Version) do
  begin
    if (Version[Index] >= '0') and (Version[Index] <= '9') then
    begin
      HasDigit := True;
      if Version[Index] <> '0' then
        HasNonZeroDigit := True;
    end
    else if Version[Index] <> '.' then
    begin
      Result := False;
      Exit;
    end;
  end;

  { Microsoft defines a missing Runtime as null, empty, or 0.0.0.0. }
  Result := HasDigit and HasNonZeroDigit;
end;

function HasWebView2Version(RootKey: Integer; SubKeyName: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKeyName, 'pv', Version) and
    IsUsableWebView2Version(Version);
  if Result then
    Log(Format('Found WebView2 Evergreen Runtime %s in %s.', [Version, SubKeyName]));
end;

function IsWebView2RuntimeInstalled(): Boolean;
begin
  if IsWin64 then
    Result := HasWebView2Version(HKLM64, WebView2Machine64Key)
  else
    Result := HasWebView2Version(HKLM32, WebView2Machine32Key);

  if not Result then
  begin
    if IsWin64 then
      Result := HasWebView2Version(HKCU64, WebView2UserKey)
    else
      Result := HasWebView2Version(HKCU32, WebView2UserKey);
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  if IsWebView2RuntimeInstalled() then
  begin
    Result := True;
    Exit;
  end;

  Log('WebView2 Evergreen Runtime is missing; blocking codexU installation.');
  MsgBox(
    'codexU 需要 Microsoft Edge WebView2 Evergreen Runtime。安装已停止，即将打开微软官方下载页面；请安装 Runtime 后重新运行本安装包。',
    mbCriticalError,
    MB_OK);

  ErrorCode := 0;
  if not ShellExec(
    'open',
    '{#WebView2RuntimeDownloadUrl}',
    '',
    '',
    SW_SHOWNORMAL,
    ewNoWait,
    ErrorCode) then
    Log(Format('Could not open the WebView2 Runtime download page (error %d).', [ErrorCode]));

  Result := False;
end;
