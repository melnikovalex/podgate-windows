; PodGate installer. Build with build.ps1, which passes the version from VERSION.
;
; What it owns: files in {app}, the PodGate service, the HKLM Run value, the Installed-apps entry.
; Uninstall gives the AirPods back to stock Windows *before* deleting anything.

#ifndef AppVersion
  #error Build with build.ps1 (it passes /DAppVersion from VERSION)
#endif

; Never change this: upgrades and the Installed-apps entry are keyed on it.
#define AppGuid "F7DFF609-FE5D-4ACA-8572-AFCA5264B197"

[Setup]
AppId={{{#AppGuid}}
AppName=PodGate
AppVersion={#AppVersion}
AppVerName=PodGate {#AppVersion}
AppPublisher=PodGate
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\PodGate
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts\setup
OutputBaseFilename=PodGate-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\PodGate.exe
UninstallDisplayName=PodGate
; The app and service are stopped in PrepareToInstall; Restart Manager has nothing to add.
CloseApplications=no
SetupLogging=yes

[Files]
Source: "..\artifacts\publish\PodGate\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; Starts the tray app, unelevated, for every user who logs on.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PodGate"; ValueData: """{app}\PodGate.exe"""; Flags: uninsdeletevalue

[Icons]
Name: "{autoprograms}\PodGate"; Filename: "{app}\PodGate.exe"

[Run]
; create fails harmlessly on an upgrade, config then points the existing service at the new files.
Filename: "{sys}\sc.exe"; Parameters: "create PodGate binPath= ""{app}\PodGate.Service.exe"" start= auto DisplayName= PodGate"; Flags: runhidden; StatusMsg: "Installing the PodGate service..."
Filename: "{sys}\sc.exe"; Parameters: "config PodGate binPath= ""{app}\PodGate.Service.exe"" start= auto"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description PodGate ""Keeps AirPods from connecting to this PC until you ask for them."""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "failure PodGate reset= 86400 actions= restart/5000/restart/5000/restart/60000"; Flags: runhidden
Filename: "{sys}\net.exe"; Parameters: "start PodGate"; Flags: runhidden; StatusMsg: "Starting the PodGate service..."
Filename: "{app}\PodGate.exe"; Flags: nowait runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PodGate.exe"; Flags: runhidden; RunOnceId: "StopApp"
; Stopping the service blocks the device (its shutdown behaviour); the restore below undoes that.
Filename: "{sys}\net.exe"; Parameters: "stop PodGate"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{app}\PodGate.Service.exe"; Parameters: "--restore-stock"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreStock"
Filename: "{sys}\sc.exe"; Parameters: "delete PodGate"; Flags: runhidden; RunOnceId: "DeleteService"

[UninstallDelete]
Type: files; Name: "{commonappdata}\PodGate\config.json"
Type: files; Name: "{commonappdata}\PodGate\service.log"
Type: files; Name: "{commonappdata}\PodGate\state.json"
Type: dirifempty; Name: "{commonappdata}\PodGate"
Type: filesandordirs; Name: "{localappdata}\PodGate"

[Code]
function VersionValue(Version: String): Int64;
var
  Part: Integer;
  Dot: Integer;
begin
  Result := 0;
  Dot := Pos('-', Version);
  if Dot > 0 then Version := Copy(Version, 1, Dot - 1);
  for Part := 1 to 3 do
  begin
    Dot := Pos('.', Version);
    if Dot = 0 then
    begin
      Result := Result * 100000 + StrToIntDef(Version, 0);
      Version := '';
    end
    else
    begin
      Result := Result * 100000 + StrToIntDef(Copy(Version, 1, Dot - 1), 0);
      Version := Copy(Version, Dot + 1, Length(Version));
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  Installed: String;
begin
  Result := True;
  if RegQueryStringValue(HKLM64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1', 'DisplayVersion', Installed) and
     (VersionValue(Installed) > VersionValue('{#AppVersion}')) then
  begin
    SuppressibleMsgBox('PodGate ' + Installed + ' is already installed, which is newer than {#AppVersion}.' + #13#10 +
      'Uninstall it first if you really want the older version.', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure Run(FileName, Params: String);
var
  Code: Integer;
begin
  Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  // An upgrade replaces files the running app and service hold open.
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PodGate.exe');
  Run(ExpandConstant('{sys}\net.exe'), 'stop PodGate');
end;
