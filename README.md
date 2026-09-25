# MozillaPDF

PDF-Betrachter für Windows auf Basis von [PDF.js](https://github.com/mozilla/pdf.js) im WebView2 – mit dem Annotationseditor von
PDF.js zum **Hervorheben**, für Freitext, Freihandzeichnungen, Bilder und Unterschriften. Die Anmerkungen werden beim Speichern als
echte PDF-Anmerkungen mit Darstellungsstrom in die Datei geschrieben und sind in anderen Betrachtern sichtbar (geprüft mit dem
Chromium-Viewer von PDFlight). Gedacht auch als externes Programm für PDFlight („Programme“-Menü).

PDF.js läuft vollständig lokal; das Programm geht nicht ins Netz.

## Bedienen

Es gibt keine eigene Symbolleiste und keine Statuszeile – die Leiste von PDF.js bringt alles mit. Fehler meldet ein Dialog.

- **Öffnen:** Strg+O oder „Öffnen“ im Menü „»“ rechts in der Leiste; beides führt zum Öffnen-Dialog der App. Oder die Datei als
  Aufrufparameter: `MozillaPDF.exe datei.pdf`, oder per Drag & Drop ins Fenster – bei mehreren Dateien öffnet die erste hier, jede
  weitere in einem eigenen Fenster.
- **Hervorheben:** Marker in der Leiste wählen, Text mit der Maus markieren. Daneben Text, Zeichnen, Bild und Unterschrift.
- **Eigene Tastaturkürzel:** Strg+H „Hervorheben“ und Strg+T „Text“ – der erste Druck wählt das Werkzeug samt seiner Leiste, der
  zweite schaltet es ab und schließt die Leiste (auch mitten im Tippen). Strg+G springt ins Seitenfeld und
  markiert die Zahl – Seitenzahl tippen, Enter. „Weitersuchen“ geht dann mit F3 oder Strg+Umschalt+G statt Strg+G.
- **Speichern:** Speichern-Knopf der Leiste oder Strg+S; der Dialog „Dokument speichern“ schlägt die angezeigte Datei
  selbst vor (Überschreiben mit Rückfrage), ein anderer Name ist möglich – danach zeigt das Fenster die neue Datei.
- **Schließen** mit ungespeicherten Hervorhebungen oder Anmerkungen fragt nach: Speichern, Nicht speichern oder Abbrechen. Beim
  Öffnen einer anderen Datei bietet PDF.js das Speichern von sich aus an.
- **Programminformationen:** „?“ rechts in der Leiste (vor dem Menü „»“) oder F1 – kurze Beschreibung, Programm- und
  PDF.js-Version, Autor und Lizenz. Ein Fragezeichen in der Titelleiste gibt es bewusst nicht: Windows zeigt es nur ohne Minimieren-
  und Maximieren-Knopf. Wie man eine Datei öffnet, steht auf der leeren Fläche, solange kein Dokument geladen ist.
- Lage, Größe und Maximiert-Zustand des Fensters merkt `%APPDATA%\MozillaPDF\settings.json`.

Das Programm wird nur auf Deutsch gepflegt (Oberfläche, Installer, PDF.js-Sprache).

## Bauen

1. `update-pdfjs.ps1` ausführen – lädt die aktuelle PDF.js-Distribution nach `pdfjs\` (Ordner ist git-ignoriert).
2. `dotnet build MozillaPDF.csproj -c Release -p:Platform=x64` – `pdfjs\` wird neben die EXE kopiert; der Installer nimmt
   `bin\x64\Release\net10.0-windows`.
3. Installer: `"C:\Program Files\Inno Setup 7\ISCC.exe" Installer.iss` → `MozillaPDFSetup.exe`.

## PDF.js aktualisieren

```
.\update-pdfjs.ps1 -CheckOnly   # nur prüfen (Exitcode 2 = neuere Version verfügbar)
.\update-pdfjs.ps1              # prüfen und bei Bedarf ersetzen
```

Das Skript prüft den Download gegen die SHA-256-Prüfsumme von GitHub und ersetzt den Ordner erst, wenn das neue Archiv vollständig
entpackt und geprüft ist. Danach neu bauen und den Installer neu erzeugen.

## Aufbau

- `Forms\MainForm` – nur das WebView, keine eigene Symbolleiste und keine Statuszeile. Der Viewer (`pdfjs\web\viewer.html`) läuft unter dem virtuellen Host
  `https://pdfjs.local`; die Datei geht als SharedBuffer an die Seite und von dort per `PDFViewerApplication.open({ data })` an PDF.js.
- `Classes\AppSettings` – Fensterlage als JSON.
- `make-icon.ps1` – erzeugt `MozillaPDF.ico` aus `pdfjs-logo.svg`; Edge rendert jede Größe einzeln (16 bis 256 px). Bei Änderungen
  auch das Icon in `Forms\MainForm.resx` erneuern.

## Lizenzen

MozillaPDF steht wie PDF.js unter der Apache-Lizenz 2.0 (`LICENSE`, Copyright-Vermerk in `NOTICE`); der Installer zeigt die Lizenz
vor der Installation. PDF.js bringt seine Lizenz in `pdfjs\LICENSE` mit. Das Programm-Icon ist das PDF.js-Logo aus demselben Projekt. „Mozilla“ ist eine Marke der Mozilla Foundation;
MozillaPDF ist kein Produkt von Mozilla.
