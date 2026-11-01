; Better Download Manager - per-user installer (no administrator rights needed).
; Build with tools\build-release.ps1, which publishes the apps into dist\publish first.

#define AppName "Better Download Manager"
#define AppVersion "0.2.0"
#define ExtensionId "depjhibopdmbilmmphmipdklabicghkf"

[Setup]
AppId={{B7D3A1E2-5C41-4F0B-9E6A-2D8F3B1C7A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Farzonline
DefaultDirName={localappdata}\Programs\BetterDownloadManager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=BDM-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\BDM.App\Assets\bdm.ico
UninstallDisplayIcon={app}\BDM.App.exe
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start Better Download Manager with Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
; Self-contained publish of the app, the browser host and the bdm command-line tool (all share one folder and one runtime).
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The browser extension, loaded unpacked from this stable folder.
Source: "..\extension\*"; DestDir: "{app}\extension"; Excludes: "test,node_modules,.manifest-key.tmp,package.json"; Flags: ignoreversion recursesubdirs createallsubdirs
; Optional: drop ffmpeg.exe / ffprobe.exe (and yt-dlp.exe) into installer\vendor before building to bundle them.
Source: "vendor\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "vendor\ffprobe.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "vendor\yt-dlp.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\BDM.App.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\BDM.App.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "BetterDownloadManager"; \
  ValueData: """{app}\BDM.App.exe"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Tell Chrome, Edge and Brave where the native messaging host lives (per user, no admin).
Filename: "{app}\bdm-host.exe"; Parameters: "--register {#ExtensionId} chrome edge brave"; Flags: runhidden; StatusMsg: "Connecting to your browsers..."
Filename: "{app}\BDM.App.exe"; Description: "Start {#AppName}"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
; Written by "bdm-host --register" after installation, so the installer does not know about it.
Type: files; Name: "{app}\com.bdm.host.json"

[UninstallRun]
Filename: "{app}\bdm-host.exe"; Parameters: "--unregister chrome edge brave"; Flags: runhidden; RunOnceId: "UnregisterHost"

[Code]
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'One step is left in your browser: open chrome://extensions, switch on Developer mode, choose "Load unpacked" and pick' + #13#10 +
      ExpandConstant('{app}\extension');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\BetterDownloadManager');
    if DirExists(DataDir) then
      if MsgBox('Also delete your Better Download Manager settings, download list and saved passwords?' + #13#10 +
                'Your downloaded files are not touched.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
