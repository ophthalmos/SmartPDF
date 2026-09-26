; ============================================================================
; MoziPDF – Installer (Inno Setup 7)
; Vorher: Release bauen (dotnet build MoziPDF.csproj -c Release -p:Platform=x64), dann dieses Skript mit
; "C:\Program Files\Inno Setup 7\ISCC.exe" Installer.iss kompilieren. Ergebnis: MoziPDFSetup.exe im Projektordner.
; ============================================================================

#define appName "MoziPDF"
#define releaseDir "bin\x64\Release\net10.0-windows"
#define appVersion GetVersionNumbersString(releaseDir + "\MoziPDF.exe")

[Setup]
AppId={{3F6C2E8D-9A41-4B57-8C2E-7D15A90B64E3}
AppName={#appName}
AppVersion={#appVersion}
AppVerName={#appName} {#appVersion} (64-Bit)
VersionInfoVersion={#appVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppPublisher=Wilhelm Happe
AppCopyright=© 2026 W. Happe
LicenseFile=LICENSE
UsePreviousAppDir=yes
DefaultDirName={autopf}\{#appName}
DefaultGroupName={#appName}
DisableWelcomePage=yes
DisableReadyPage=yes
DisableProgramGroupPage=yes
SetupIconFile=MoziPDF.ico
UninstallDisplayIcon={app}\{#appName}.exe
OutputDir=.
OutputBaseFilename={#appName}Setup
Compression=lzma2/ultra
SolidCompression=yes
DirExistsWarning=no
CloseApplications=yes
SetupMutex={#appName}_SetupMutex
WizardStyle=modern
UsedUserAreasWarning=no

[Languages]
Name: de; MessagesFile: "compiler:Languages\German.isl"

[Messages]
de.ConfirmUninstall=Bist du sicher, dass du %1 und alle zugehörigen Komponenten entfernen möchtest? Vor einem Update ist keine Deinstallation erforderlich.

[CustomMessages]
de.Run={#appName} starten
de.DesktopIcon=Verknüpfung auf dem Desktop anlegen
de.WebView2Missing=Die Microsoft-WebView2-Runtime wurde nicht gefunden.%n%n{#appName} benötigt sie für die PDF-Anzeige. Bitte lade sie herunter von:%nhttps://developer.microsoft.com/microsoft-edge/webview2/%n%nDie Installation wird trotzdem fortgesetzt.

[Tasks]
Name: desktopicon; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
; Programm samt LICENSE und NOTICE (Apache 2.0) und PDF.js-Ordner (pdfjs\, mit dessen LICENSE); Quelltext-Karten (*.map) und
; Debug-Symbole bleiben draußen
Source: "{#releaseDir}\*"; Excludes: "*.pdb,*.map"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Beim Update eine ältere PDF.js-Version vollständig ersetzen – sonst blieben Dateien, die es in der neuen nicht mehr gibt
Type: filesandordirs; Name: "{app}\pdfjs"

[Icons]
Name: "{group}\{#appName}"; Filename: "{app}\{#appName}.exe"
Name: "{autodesktop}\{#appName}"; Filename: "{app}\{#appName}.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\{#appName}.exe"; Description: "{cm:Run}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  WebView2Version: String;
begin
  Result := True;
  { WebView2-Runtime vorhanden? (.NET-Runtime prüft Windows beim ersten Start selbst) }
  if not RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', WebView2Version) then
    if not RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', WebView2Version) then
      MsgBox(CustomMessage('WebView2Missing'), mbInformation, MB_OK);
end;
