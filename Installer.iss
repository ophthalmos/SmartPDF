; ============================================================================
; SmartPDF – Installer (Inno Setup 7)
; Vorher: Release bauen (dotnet build SmartPDF.csproj -c Release -p:Platform=x64), dann dieses Skript mit
; "C:\Program Files\Inno Setup 7\ISCC.exe" Installer.iss kompilieren. Ergebnis: SmartPDFSetup.exe im Projektordner.
; ============================================================================

#define appName "SmartPDF"
#define releaseDir "bin\x64\Release\net10.0-windows"
#define appVersion GetVersionNumbersString(releaseDir + "\SmartPDF.exe")

[Setup]
; Neue AppId seit der Umbenennung in SmartPDF (27.09.2026): SmartPDF installiert frisch nach {autopf}\SmartPDF; eine
; Installation unter einem früheren Programmnamen bleibt davon unberührt und wird von Hand deinstalliert (mit derselben AppId
; landete das Update wegen UsePreviousAppDir im alten Ordner, samt EXE und Verknüpfungen unter dem alten Namen).
AppId={{AD277654-32CC-46ED-990F-0D8F1DC5E513}
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
SetupIconFile=SmartPDF.ico
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
; Die Registry-Einträge für „Öffnen mit“ ändern Dateizuordnungen – der Explorer soll sie sofort übernehmen
ChangesAssociations=yes

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
; Programm samt LICENSE (Apache 2.0), Hilfe-PDF und PDF.js-Ordner (pdfjs\, mit dessen LICENSE); Quelltext-Karten (*.map) und
; Debug-Symbole bleiben draußen
Source: "{#releaseDir}\*"; Excludes: "*.pdb,*.map"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Beim Update eine ältere PDF.js-Version vollständig ersetzen – sonst blieben Dateien, die es in der neuen nicht mehr gibt
Type: filesandordirs; Name: "{app}\pdfjs"

[Icons]
Name: "{group}\{#appName}"; Filename: "{app}\{#appName}.exe"
Name: "{autodesktop}\{#appName}"; Filename: "{app}\{#appName}.exe"; Tasks: desktopicon

[Registry]
; SmartPDF im Explorer unter „Öffnen mit“ für PDF-Dateien anbieten – ohne die Standard-App für PDFs zu ändern (die wählt der
; Nutzer selbst). HKA = HKLM, da das Setup mit Administratorrechten läuft; das Deinstallieren entfernt alles wieder.
; 1) Das Programm selbst: Name in der Liste, unterstützter Typ, Öffnen-Befehl
Root: HKA; Subkey: "Software\Classes\Applications\{#appName}.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#appName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#appName}.exe\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#appName}.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#appName}.exe"" ""%1"""
; 2) Eigene ProgID mit Symbol und Öffnen-Befehl
Root: HKA; Subkey: "Software\Classes\{#appName}.pdf"; ValueType: string; ValueName: ""; ValueData: "PDF-Dokument"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#appName}.pdf"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "PDF-Dokument"
Root: HKA; Subkey: "Software\Classes\{#appName}.pdf\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#appName}.exe,0"
Root: HKA; Subkey: "Software\Classes\{#appName}.pdf\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#appName}.exe"" ""%1"""
; 3) Als Kandidat für .pdf eintragen – erscheint damit in „Öffnen mit“, ohne Standard zu werden
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "{#appName}.pdf"; ValueData: ""; Flags: uninsdeletevalue

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
