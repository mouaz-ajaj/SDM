; SDM — Speed Download Manager
;
; A per-user installer. Nothing here needs administrator rights, and nothing is written
; outside the user's own profile — which is not a convenience but a matter of keeping the
; installation and the application consistent with each other. SDM already keeps its
; settings, database, logs and browser registration per user; installing it for the whole
; machine would produce something installed once and configured separately by every person
; who ran it.
;
; Build it with:
;   dotnet publish src/SDM.Desktop/SDM.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish
;   iscc installer/sdm.iss

#define AppName        "SDM"
#define AppFullName    "SDM — Speed Download Manager"
#define AppVersion     "0.2.0"
#define AppPublisher   "SDM Contributors"
#define AppUrl         "https://github.com/mouaz-ajaj/SDM"
#define PublishDir     "..\publish"

[Setup]
AppId={{9E3B7C41-6A2D-4F58-9C0E-5D1A8B34F7E2}
AppName={#AppFullName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per user, so no UAC prompt and no administrator account required.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

OutputDir=..\artifacts
OutputBaseFilename=SDM-Setup-{#AppVersion}
SetupIconFile=..\src\SDM.Desktop\Assets\Branding\sdm.ico
UninstallDisplayIcon={app}\SDM.Desktop.exe
WizardStyle=modern

; The payload is a self-contained .NET application, so it is mostly compressible runtime.
Compression=lzma2/max
SolidCompression=yes

; Windows 10 or later, matching what the application is tested on.
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup";     Description: "Start SDM when I sign in";   GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; Everything except debug symbols. libSkiaSharp.pdb alone is eighty megabytes of symbols
; for a library we did not write and cannot debug, and shipping it would double the
; download for nothing.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; \
    Excludes: "*.pdb"

; Ours are kept, and they are kilobytes. Without them a crash report names methods and no
; line numbers, which is the difference between a report worth reading and one that is not.
Source: "{#PublishDir}\SDM.*.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; The browser extension travels with the application, because Chrome loads it from a
; folder and remembers that folder. Somewhere under the installation is the one place it
; will still be after an update.
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\SDM.Desktop.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";     Filename: "{app}\SDM.Desktop.exe"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}";     Filename: "{app}\SDM.Desktop.exe"; Tasks: startup

[Run]
; Registering the browser bridge is done by the application, not by this installer.
;
; A script would have been the obvious choice and would have been wrong twice: script
; execution is disabled by policy on a great many Windows machines, and the manifest holds
; absolute paths — a user whose name is not in the installer's code page has a profile
; folder that an installer's own ANSI file writing turns into nonsense. The host writes it
; as UTF-8, from .NET, where the path is a string rather than a guess.
Filename: "{app}\SDM.NativeHost.exe"; Parameters: "--register"; \
    Flags: runhidden waituntilterminated; StatusMsg: "Registering the browser bridge..."

Filename: "{app}\SDM.Desktop.exe"; Description: "Start {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Before the files go, while the executable that knows how to undo it still exists.
Filename: "{app}\SDM.NativeHost.exe"; Parameters: "--unregister"; \
    Flags: runhidden waituntilterminated; RunOnceId: "UnregisterBrowsers"

[UninstallDelete]
; The installation folder only. Settings, the transfer list and the logs live in
; %LOCALAPPDATA%\SDM and are deliberately left alone: someone reinstalling should find
; their downloads still listed, and someone leaving can delete one folder.
Type: filesandordirs; Name: "{app}\extension"

[Code]
{ SDM holds a single-instance mutex per user. Installing over a running copy would fail to
  replace files that are open, so it is stopped first — and the same on the way out. }
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec(ExpandConstant('{cmd}'), '/C taskkill /IM SDM.Desktop.exe /F', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{cmd}'), '/C taskkill /IM SDM.Desktop.exe /F', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
