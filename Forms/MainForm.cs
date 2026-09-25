using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using MozillaPDF.Classes;

namespace MozillaPDF.Forms;

/// <summary>PDF-Betrachter mit Hervorheben und Anmerkungen auf Basis von PDF.js (Mozilla) im WebView2.
/// <list type="bullet">
/// <item>Der fertige Viewer der Distribution (pdfjs\web\viewer.html) läuft unter dem virtuellen Host https://pdfjs.local, ohne Datei
/// (<c>?file=</c> leer). Alles liegt lokal – PDF.js geht nicht ins Netz.</item>
/// <item>Die Datei geht als CoreWebView2SharedBuffer an die Seite; ein beim Dokumentstart eingefügtes Skript (<see cref="PageScript"/>)
/// übergibt sie mit <c>PDFViewerApplication.open({ data, filename })</c>. Keine Kopie auf der Platte – der Programmordner darf
/// schreibgeschützt sein (Installation unter „Programme“).</item>
/// <item>Keine eigene Symbolleiste und keine Statuszeile: Die Leiste von PDF.js bringt alles mit. Das Seitenskript leitet nur das
/// Öffnen (Knopf im Menü „»“, Strg+O) auf den Öffnen-Dialog der App um, weil die App den Pfad fürs Speichern kennen muss, und ergänzt
/// einen „?“-Knopf samt F1 für die Programminformationen (<see cref="ShowHelp"/>). Fehler meldet ein Dialog.</item>
/// <item>Speichern (Speichern-Knopf der Leiste, Strg+S) bettet die Hervorhebungen ein und löst einen Download aus; das WebView fängt ihn
/// ab und schreibt ihn dorthin, wo der Speichern-Dialog es will. Beim Schließen mit ungespeicherten Änderungen fragt die App nach.</item>
/// <item>Lage, Größe und Maximiert-Zustand des Fensters merkt <see cref="AppSettings"/>.</item>
/// <item>Drag &amp; Drop: Das Seitenskript fängt abgelegte Dateien ab, bevor PDF.js sie selbst (ohne Pfad) öffnet, und meldet sie per
/// postMessageWithAdditionalObjects mit echtem Pfad (<see cref="OpenDroppedFiles"/>): die erste PDF in diesem Fenster, jede weitere in
/// einem neuen. <see cref="Core_NavigationStarting"/> ist das Sicherheitsnetz für Drops als file://-Navigation (Muster aus PDFlight)
/// und hält den Viewer zugleich auf seiner Seite – Weblinks im Dokument öffnen im Standardbrowser.</item>
/// <item><b>Modale Dialoge nie innerhalb eines WebView2-Rückrufs</b> (Ereignis oder Fortsetzung nach <c>await</c> auf einen
/// WebView2-Aufruf – .NET setzt dort oft direkt im Rückruf fort): WebView2 duldet einen blockierten Rückruf nur wenige Sekunden und
/// beendet dann das Programm (Absturz in EmbeddedBrowserWebView.dll, 0x80000003 – Fehlerbericht 25.09.2026: Rückfrage beim Schließen).
/// Deshalb zeigt <see cref="ShowError"/> grundsätzlich per BeginInvoke, die Rückfrage beim Schließen kommt per BeginInvoke, und der
/// Speichern-Dialog läuft über eine Zurückstellung (GetDeferral) des Download-Ereignisses.</item>
/// </list></summary>
public partial class MainForm : Form
{
    private const string Host = "pdfjs.local";
    private const string PdfJsWebsite = "https://mozilla.github.io/pdf.js/";
    private readonly string pdfjsFolder = Path.Combine(AppContext.BaseDirectory, "pdfjs");
    private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MozillaPDF");
    private readonly AppSettings settings = AppSettings.Load();
    private string? startFile;   // Datei aus der Befehlszeile, geöffnet sobald der Viewer steht
    private string? currentPath; // angezeigte Datei; Vorgabe für den Speichern-Dialog
    private bool pageReady;
    private bool closeApproved;  // Rückfrage beim Schließen erledigt: FormClosing lässt das nächste Schließen durch
    private bool closeAfterSave; // „Speichern“ in der Rückfrage: nach dem fertigen Download schließen

    public MainForm(string[] args)
    {
        InitializeComponent();
        startFile = args.FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        RestoreWindowBounds();
    }

    // ==== Fensterlage

    /// <summary>Gemerkte Lage und Größe übernehmen – nur, wenn das Fenster auf einem der vorhandenen Bildschirme ausreichend sichtbar ist
    /// (Monitor abgezogen, Auflösung geändert). Maximiert wird über die normale Lage, damit „Wiederherstellen“ dorthin zurückkehrt.</summary>
    private void RestoreWindowBounds()
    {
        if (settings.WindowWidth <= 0 || settings.WindowHeight <= 0) { return; }
        var bounds = new Rectangle(settings.WindowX, settings.WindowY,
            Math.Max(settings.WindowWidth, MinimumSize.Width), Math.Max(settings.WindowHeight, MinimumSize.Height));
        var visible = Screen.AllScreens.Any(s => { var part = Rectangle.Intersect(s.WorkingArea, bounds); return part.Width >= 200 && part.Height >= 100; });
        if (!visible) { return; }
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        if (settings.WindowMaximized) { WindowState = FormWindowState.Maximized; }
    }

    private void SaveWindowBounds()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds; // maximiert/minimiert: die normale Lage merken
        (settings.WindowX, settings.WindowY, settings.WindowWidth, settings.WindowHeight) = (bounds.X, bounds.Y, bounds.Width, bounds.Height);
        settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        settings.Save();
    }

    // ==== Start

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        if (!File.Exists(Path.Combine(pdfjsFolder, "web", "viewer.html")))
        {
            ShowError("PDF.js-Distribution nicht gefunden",
                "Erwartet wird der Ordner „pdfjs“ (mit web\\viewer.html und build\\pdf.mjs) neben der EXE." + Environment.NewLine
                + "Das Skript update-pdfjs.ps1 im Projektordner lädt die aktuelle Version von GitHub; danach neu bauen.");
            return;
        }
        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                Language = "de-DE", // PDF.js übernimmt die Sprache (locale/de)
                IsCustomCrashReportingEnabled = true, // Absturzberichte bleiben lokal
                AdditionalBrowserArguments = "--metrics-recording-only --disable-background-networking --disable-domain-reliability --disable-component-update",
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataFolder, "WebView2"), options);
            await webView.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            ShowError("WebView2 konnte nicht gestartet werden", ex.Message);
            return;
        }
        var core = webView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsReputationCheckingRequired = false; // kein SmartScreen-Abgleich
        core.SetVirtualHostNameToFolderMapping(Host, pdfjsFolder, CoreWebView2HostResourceAccessKind.Allow); // Viewer, Worker, Schriften, CMaps, Locale
        core.DownloadStarting += Core_DownloadStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.NavigationStarting += Core_NavigationStarting;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript);
        core.Navigate($"https://{Host}/web/viewer.html?file="); // leer: der Viewer startet ohne Dokument (sonst zeigt er sein Beispiel)
    }

    /// <summary>Läuft in der Viewer-Seite vor deren eigenen Skripten:
    /// <list type="bullet">
    /// <item>nimmt die Datei als SharedBuffer entgegen, übergibt sie an PDF.js und meldet „opened“ bzw. „error“;</item>
    /// <item>ersetzt die unsichtbare Dateiauswahl des Viewers (<c>_openFileInput</c>): Öffnen-Knopf im Menü „»“ und Strg+O rufen beide nur
    /// deren click() auf (onOpenFile, PDF.js 6.3) – statt des Browser-Dialogs kommt „openRequest“ an die App;</item>
    /// <item>setzt vor das Menü „»“ einen „?“-Knopf im Stil der Leiste (Symbol als CSS-Maske wie die übrigen Knöpfe) und fängt F1 ab –
    /// beides meldet „help“;</item>
    /// <item>eigene Kürzel: Strg+H „Hervorheben“ und Strg+T „Text“ als Umschalter, Strg+G springt ins Seitenfeld;</item>
    /// <item>zeigt auf der leeren Fläche, wie man eine Datei öffnet, und sperrt Speichern und Drucken (Knöpfe, Strg+S, Strg+P), bis das
    /// erste Dokument steht;</item>
    /// <item>fängt abgelegte Dateien in der Einfangphase ab (vor dem eigenen Drop von PDF.js, der den Pfad verlöre), zeigt beim Ziehen
    /// einen gestrichelten Rahmen und meldet „drop“ samt den Dateien als Zusatzobjekte.</item>
    /// </list></summary>
    private const string PageScript = """
        (() => {
          if (window.top !== window) { return; }
          const post = m => chrome.webview.postMessage(m);
          const app = () => new Promise(resolve => {
            const wait = () => window.PDFViewerApplication ? resolve(window.PDFViewerApplication) : setTimeout(wait, 20);
            wait();
          });
          const hideHint = () => document.getElementById("mozEmptyHint")?.remove();
          // Speichern und Drucken ohne Dokument laufen in PDF.js in einen Fehler, nachdem es die Klasse „wait“ gesetzt hat – deren
          // unsichtbare Fläche mit Eieruhr über dem ganzen Fenster bliebe stehen und nähme jeden Klick (Fehlerbericht 25.09.2026).
          // Deshalb sind beide gesperrt, bis das erste Dokument steht.
          let hasDocument = false;
          const outputButtons = ["printButton", "downloadButton", "secondaryPrint", "secondaryDownload"];
          const enableOutput = on => outputButtons.forEach(id => { const b = document.getElementById(id); if (b) { b.disabled = !on; } });
          chrome.webview.addEventListener("sharedbufferreceived", async e => {
            const shared = e.getBuffer();
            const data = new Uint8Array(shared.slice(0)); // eigene Kopie – der geteilte Puffer wird gleich freigegeben
            chrome.webview.releaseBuffer(shared);
            const viewer = await app();
            await viewer.initializedPromise;
            try {
              await viewer.open({ data, filename: (e.additionalData || {}).fileName });
              hideHint();
              hasDocument = true; enableOutput(true);
              post({ type: "opened", pages: viewer.pagesCount });
            } catch (err) { post({ type: "error", message: String(err && err.message || err) }); }
          });
          (async () => {
            const viewer = await app();
            await viewer.initializedPromise;
            const input = () => new Promise(resolve => {
              const wait = () => viewer._openFileInput ? resolve() : setTimeout(wait, 20);
              wait();
            });
            await input(); // legt run() erst nach der Initialisierung an
            viewer._openFileInput = { click: () => post({ type: "openRequest" }) };
          })();
          // Rahmen, solange Dateien über dem Fenster sind. Ein Zähler aus dragenter/dragleave taugt nicht: Chromium meldet schon beim
          // Hereinziehen ein dragleave, das ihn sofort ausgleicht (geprüft 25.09.2026). Deshalb: jedes dragover frischt den Rahmen auf,
          // er endet beim Ablegen, wenn der Zeiger das Fenster verlässt, oder eine Sekunde nach dem letzten dragover (Abbruch mit Esc
          // meldet kein dragleave; beim Stillhalten wiederholt Chromium dragover laufend, dort erlischt der Rahmen also nicht).
          let dragTimer = 0;
          const hasFiles = e => [...(e.dataTransfer?.types || [])].includes("Files");
          const endDrag = () => { clearTimeout(dragTimer); document.body.classList.remove("mozDrop"); };
          window.addEventListener("dragover", e => {
            if (!hasFiles(e)) { return; }
            e.preventDefault(); e.stopImmediatePropagation();
            e.dataTransfer.dropEffect = "copy";
            document.body.classList.add("mozDrop");
            clearTimeout(dragTimer); dragTimer = setTimeout(endDrag, 1000);
          }, true);
          window.addEventListener("dragleave", e => {
            const outside = e.clientX <= 0 || e.clientY <= 0 || e.clientX >= innerWidth || e.clientY >= innerHeight;
            if (hasFiles(e) && outside) { endDrag(); }
          }, true);
          window.addEventListener("drop", e => {
            if (!hasFiles(e)) { return; }
            e.preventDefault(); e.stopImmediatePropagation();
            endDrag();
            const files = [...e.dataTransfer.files];
            if (files.length) { chrome.webview.postMessageWithAdditionalObjects("drop", files); }
          }, true);
          window.addEventListener("keydown", e => {
            const output = (e.ctrlKey || e.metaKey) && !e.altKey && ["s", "p"].includes(e.key.toLowerCase());
            if (output && !hasDocument) { e.preventDefault(); e.stopImmediatePropagation(); return; } // Strg+S, Strg+P: s. hasDocument
            // Eigene Kürzel (Wunsch vom 25.09.2026): Strg+H „Hervorheben“, Strg+T „Text“ – wie ein Klick auf den Knopf, also als Umschalter:
            // der erste Druck wählt das Werkzeug samt seiner Leiste, der zweite schaltet es ab und schließt die Leiste (PDF.js schaltet beim
            // Klick auf den aktiven Knopf zurück auf „kein Werkzeug“). Strg+G springt ins Seitenfeld und markiert die Zahl; es ersetzt
            // damit „Weitersuchen“ von PDF.js – das bleibt über F3, Enter im Suchfeld und Strg+Umschalt+G erreichbar.
            const plainCtrl = e.ctrlKey && !e.altKey && !e.shiftKey && !e.metaKey;
            const tools = { h: "editorHighlightButton", t: "editorFreeTextButton" };
            const key = e.key.toLowerCase();
            if (plainCtrl && (key in tools || key === "g")) {
              e.preventDefault(); e.stopImmediatePropagation();
              if (!hasDocument) { return; }
              if (key in tools) {
                const tool = document.getElementById(tools[key]);
                if (!tool || tool.disabled) { return; }
                // Mitten im Tippen (Textfeld hat den Fokus) übernähme der Klick nur den Text, das Werkzeug bliebe an (geprüft 25.09.2026):
                // erst das Feld abgeben, dann umschalten.
                const editing = document.activeElement?.closest?.(".annotationEditorLayer");
                if (editing) { document.activeElement.blur(); setTimeout(() => tool.click(), 0); } else { tool.click(); }
              } else {
                const page = document.getElementById("pageNumber");
                if (page) { page.focus(); page.select(); }
              }
              return;
            }
            if (e.key !== "F1") { return; }
            e.preventDefault(); e.stopPropagation();
            post({ type: "help" });
          }, true);
          document.addEventListener("DOMContentLoaded", () => {
            const icon = `url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Ccircle cx='8' cy='8' r='6.9' fill='none' stroke='black' stroke-width='1.2'/%3E%3Cpath d='M6 6.3a2 2 0 1 1 2.9 1.8c-.6.3-.9.8-.9 1.4v.4' fill='none' stroke='black' stroke-width='1.3' stroke-linecap='round'/%3E%3Ccircle cx='8' cy='11.7' r='.85'/%3E%3C/svg%3E")`;
            // Die Content-Security-Policy des Viewers (style-src 'self') sperrt eingefügte <style>-Elemente; ein per Skript erzeugtes
            // Stylesheet (CSSOM) ist davon nicht betroffen, Daten-URLs für Bilder erlaubt sie (img-src data:).
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(`#mozHelpButton::before { -webkit-mask-image: ${icon}; mask-image: ${icon}; }
              #mozEmptyHint { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; text-align: center;
                pointer-events: none; font: message-box; font-size: 15px; line-height: 1.6; color: var(--main-color); opacity: .75; }
              body.mozDrop::after { content: ""; position: fixed; inset: calc(var(--toolbar-height) + 8px) 8px 8px; z-index: 100000;
                border: 3px dashed #7aa7d4; border-radius: 6px; pointer-events: none; }`);
            document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
            if (!hasDocument) { enableOutput(false); }
            const toggle = document.getElementById("secondaryToolbarToggle");
            if (toggle) {
              const help = document.createElement("button");
              help.id = "mozHelpButton"; help.className = "toolbarButton"; help.type = "button"; help.tabIndex = 0;
              help.title = "Über MozillaPDF (F1)";
              help.innerHTML = "<span>Über MozillaPDF</span>";
              help.addEventListener("click", () => post({ type: "help" }));
              toggle.parentNode.insertBefore(help, toggle);
            }
            const container = document.getElementById("viewerContainer");
            if (container) {
              const hint = document.createElement("div");
              hint.id = "mozEmptyHint";
              hint.innerHTML = "<div><b>Kein Dokument geöffnet</b><br>Öffnen mit Strg+O, im Menü » rechts oben oder per Drag &amp; Drop<br>Programminformationen mit F1 oder ?</div>";
              container.parentNode.appendChild(hint);
            }
          });
        })();
        """;

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) { ShowError("Viewer konnte nicht geladen werden", e.WebErrorStatus.ToString()); return; }
        if (pageReady) { return; }
        pageReady = true;
        if (startFile != null) { var file = startFile; startFile = null; BeginInvoke(() => ShowFile(file)); }
    }

    // ==== Datei anzeigen und speichern

    /// <summary>Datei als SharedBuffer an die Viewer-Seite schicken; das Seitenskript übergibt sie an PDF.js.</summary>
    private void ShowFile(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"„{Path.GetFileName(path)}“ konnte nicht gelesen werden", ex.Message);
            return;
        }
        var core = webView.CoreWebView2;
        using var buffer = core.Environment.CreateSharedBuffer((ulong)Math.Max(bytes.Length, 1));
        using (var stream = buffer.OpenStream()) { stream.Write(bytes); }
        core.PostSharedBufferToScript(buffer, CoreWebView2SharedBufferAccess.ReadOnly, JsonSerializer.Serialize(new { fileName = Path.GetFileName(path) }));
        currentPath = path;
        Text = Path.GetFileName(path) + " – MozillaPDF";
    }

    /// <summary>Meldungen des Seitenskripts: opened, error, openRequest (Öffnen-Knopf oder Strg+O im Viewer), help („?“-Knopf oder F1).
    /// Dialoge erst nach dem Nachrichten-Callback zeigen (BeginInvoke).</summary>
    private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.AdditionalObjects is { Count: > 0 } objects) // „drop“: die abgelegten Dateien mit echtem Pfad
        {
            var paths = objects.OfType<CoreWebView2File>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            BeginInvoke(() => OpenDroppedFiles(paths));
            return;
        }
        using var message = JsonDocument.Parse(e.WebMessageAsJson);
        var root = message.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { return; }
        switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
        {
            case "opened": FocusViewer(); break;
            case "openRequest": BeginInvoke(OpenFile); break;
            case "help": BeginInvoke(ShowHelp); break;
            case "error":
                var text = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                ShowError($"„{Path.GetFileName(currentPath)}“ konnte nicht angezeigt werden", text);
                break;
        }
    }

    /// <summary>Der Viewer „lädt herunter“ (Speichern-Knopf der Leiste oder Strg+S): Ziel abfragen, Download dorthin lenken. Der Dialog
    /// darf nicht im Ereignis selbst laufen (siehe Klassenkommentar) – deshalb zurückstellen, nach dem Rückruf fragen, dann freigeben.</summary>
    private void Core_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true; // keine Download-Leiste des Browsers
        var deferral = e.GetDeferral();
        BeginInvoke(() =>
        {
            try { AskDownloadTarget(e); }
            finally { deferral.Complete(); }
        });
    }

    private void AskDownloadTarget(CoreWebView2DownloadStartingEventArgs e)
    {
        using SaveFileDialog dialog = new()
        {
            Filter = "PDF-Dateien (*.pdf)|*.pdf",
            InitialDirectory = Path.GetDirectoryName(currentPath) ?? "",
            FileName = currentPath == null ? "dokument.pdf" : Path.GetFileName(currentPath), // meist will man das Dokument selbst speichern
            Title = "Dokument speichern",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) { e.Cancel = true; closeAfterSave = false; return; }
        e.ResultFilePath = dialog.FileName;
        e.DownloadOperation.StateChanged += (s, args) =>
        {
            if (e.DownloadOperation.State == CoreWebView2DownloadState.Completed)
            {
                currentPath = e.DownloadOperation.ResultFilePath; // „Speichern unter“: Titel und nächster Vorschlag folgen der neuen Datei
                Text = Path.GetFileName(currentPath) + " – MozillaPDF";
                if (closeAfterSave) { closeApproved = true; BeginInvoke(Close); } // Speichern kam aus der Rückfrage beim Schließen
            }
            else if (e.DownloadOperation.State == CoreWebView2DownloadState.Interrupted)
            {
                closeAfterSave = false; // nicht gespeichert – das Fenster bleibt offen
                ShowError("Speichern fehlgeschlagen", e.DownloadOperation.InterruptReason.ToString());
            }
        };
    }

    /// <summary>Abgelegte Dateien öffnen: die erste PDF in diesem Fenster, jede weitere in einem neuen MozillaPDF-Fenster (ein Dokument je
    /// Fenster). Keine PDF dabei: Hinweis.</summary>
    private void OpenDroppedFiles(IReadOnlyList<string> paths)
    {
        var pdfs = paths.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        if (pdfs.Count == 0) { ShowError("Keine PDF-Datei", "MozillaPDF öffnet nur PDF-Dateien."); return; }
        if (!pageReady) { startFile ??= pdfs[0]; } else { ShowFile(pdfs[0]); }
        foreach (var more in pdfs.Skip(1))
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { ArgumentList = { more } }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowError($"„{Path.GetFileName(more)}“ konnte nicht geöffnet werden", ex.Message); }
        }
    }

    /// <summary>Der Viewer bleibt auf seiner Seite: eine Datei, die doch als file://-Navigation ankommt (Drop, bevor das Seitenskript steht),
    /// wird wie ein Drop geöffnet; Weblinks im Dokument öffnen im Standardbrowser statt den Viewer zu ersetzen.</summary>
    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? "";
        if (uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { return; }
        e.Cancel = true;
        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { var path = new Uri(uri).LocalPath; BeginInvoke(() => OpenDroppedFiles([path])); }
            catch (UriFormatException) { }
        }
        else if (uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { OpenInBrowser(uri); }
    }

    /// <summary>Links mit neuem Fenster (etwa aus dem Dokument) im Standardbrowser öffnen statt in einem nackten WebView2-Fenster.</summary>
    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { OpenInBrowser(e.Uri); }
    }

    /// <summary>Tastaturfokus in den Viewer, sobald ein Dokument steht – Bild-ab, Pfeiltasten und Strg+F wirken dann sofort, ohne erst
    /// ins Dokument zu klicken. WinForms hält das WebView womöglich schon für fokussiert, ein bloßes Focus() bliebe dann wirkungslos –
    /// deshalb erst abgeben, dann neu setzen.</summary>
    private void FocusViewer()
    {
        if (!ContainsFocus && Form.ActiveForm != this) { return; } // nicht aus dem Hintergrund nach vorn drängeln
        ActiveControl = null;
        webView.Focus();
    }

    // ==== Programminformationen

    /// <summary>„?“ in der Viewer-Leiste oder F1: kurze Programmbeschreibung, Version, PDF.js-Version, Autor und Lizenz.</summary>
    private void ShowHelp()
    {
        if (helpOpen) { return; } // F1 im Viewer kommt zweimal an: im Seitenskript und über HelpRequested
        helpOpen = true;
        try { ShowAboutDialog(); }
        finally { helpOpen = false; }
        FocusViewer();
    }

    private bool helpOpen;

    private void ShowAboutDialog()
    {
        var version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?";
        using var appIcon = Icon.ExtractIcon(Application.ExecutablePath, 0, LogicalToDeviceUnits(32));
        var page = new TaskDialogPage
        {
            Caption = "Über MozillaPDF",
            Heading = "MozillaPDF",
            Text = $"Ein einfacher PDF-Betrachter auf Basis von <a href=\"{PdfJsWebsite}\">PDF.js</a>."
                 + Environment.NewLine + Environment.NewLine
                 + "PDF.js arbeitet vollständig offline auf deinem PC.\nPDFs und Daten werden nirgendwohin übertragen.",
            Icon = appIcon != null ? new TaskDialogIcon(appIcon) : TaskDialogIcon.Information,
            Footnote = new TaskDialogFootnote
            {
                Text = $"Version {version}   •   PDF.js {PdfJsVersion() ?? "?"}   •   © 2026 Wilhelm Happe" + Environment.NewLine
                     + "MozillaPDF und PDF.js stehen unter der <a href=\"apache\">Apache-Lizenz 2.0</a>. MozillaPDF ist kein Produkt von Mozilla.",
                Icon = TaskDialogIcon.Information,
            },
            EnableLinks = true,
            AllowCancel = true, // Esc schließt – mit nur „Schließen“ ginge das sonst nicht
            Buttons = { TaskDialogButton.Close },
        };
        page.LinkClicked += (s, e) => // mehrere Links je Dialog sind möglich, unterschieden am href
        {
            if (e.LinkHref == PdfJsWebsite) { OpenInBrowser(PdfJsWebsite); }
            else { OpenLicense(Path.Combine(AppContext.BaseDirectory, "LICENSE")); }
        };
        TaskDialog.ShowDialog(this, page);
    }

    /// <summary>Webseite im Standardbrowser öffnen.</summary>
    private void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowError("Browser konnte nicht geöffnet werden", ex.Message); }
    }

    /// <summary>Lizenzdatei (ohne Endung) im Editor zeigen.</summary>
    private void OpenLicense(string path)
    {
        if (!File.Exists(path)) { ShowError("Lizenztext nicht gefunden", path); return; }
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowError("Editor konnte nicht gestartet werden", ex.Message); }
    }

    /// <summary>Version aus dem Kopfkommentar von pdfjs\build\pdf.mjs („pdfjsVersion = 6.3.289“); null, wenn nicht lesbar.</summary>
    private string? PdfJsVersion()
    {
        try
        {
            var head = string.Join("\n", File.ReadLines(Path.Combine(pdfjsFolder, "build", "pdf.mjs")).Take(60));
            var match = PdfJsVersionRegex().Match(head);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex("pdfjsVersion\\s*=\\s*\"?([0-9]+(?:\\.[0-9]+)+)")]
    private static partial Regex PdfJsVersionRegex();

    /// <summary>Fehlermeldung – grundsätzlich per BeginInvoke, weil viele Aufrufer in WebView2-Rückrufen sitzen (siehe Klassenkommentar).</summary>
    private void ShowError(string heading, string text) => BeginInvoke(() => TaskDialog.ShowDialog(this, new TaskDialogPage
    {
        Caption = "MozillaPDF",
        Heading = heading,
        Text = text,
        Icon = TaskDialogIcon.Error,
    }));

    // ==== Aktionen

    private void OpenFile()
    {
        if (!pageReady) { return; }
        using OpenFileDialog dialog = new()
        {
            Filter = "PDF-Dateien (*.pdf)|*.pdf",
            InitialDirectory = Path.GetDirectoryName(currentPath) ?? "",
            Title = "PDF-Datei öffnen",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) { ShowFile(dialog.FileName); }
    }

    // ==== Ereignisse der Oberfläche (verdrahtet in der Designer-Datei)

    /// <summary>F1, wenn der Fokus nicht im Viewer liegt (dort fängt das Seitenskript F1 ab).</summary>
    private void MainForm_HelpRequested(object? sender, HelpEventArgs e)
    {
        e.Handled = true;
        ShowHelp();
    }

    /// <summary>Fragezeichen in der Titelleiste – erscheint nur mit HelpButton = true bei MinimizeBox und MaximizeBox = false (Windows
    /// zeigt es sonst nicht). Derzeit aus, damit Minimieren und Maximieren bleiben; die Hilfe läuft über „?“ in der Viewer-Leiste und F1.</summary>
    private void MainForm_HelpButtonClicked(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true; // sonst schaltet Windows in den Kontexthilfe-Modus (Mauszeiger mit Fragezeichen)
        ShowHelp();
    }

    /// <summary>Ungespeicherte Hervorhebungen oder Anmerkungen? Die Abfrage an den Viewer ist asynchron, deshalb bricht der Handler das
    /// Schließen zunächst ab, fragt nach und schließt danach selbst erneut (<see cref="closeApproved"/>). „Speichern“ löst den Speichern-
    /// Knopf des Viewers aus; das Fenster schließt erst, wenn der Download fertig ist (Core_DownloadStarting).
    /// Beim Öffnen einer anderen Datei speichert PDF.js von sich aus (close() ruft downloadOrSave, wenn etwas geändert ist).</summary>
    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (closeApproved || !pageReady || webView.CoreWebView2 == null) { SaveWindowBounds(); return; }
        e.Cancel = true;
        var unsaved = await HasUnsavedChangesAsync();
        BeginInvoke(() => AskBeforeClose(unsaved)); // nie im Rückruf von ExecuteScriptAsync – dort beendete WebView2 nach Sekunden das Programm
    }

    private void AskBeforeClose(bool unsaved)
    {
        if (!unsaved) { closeApproved = true; Close(); return; }
        var save = new TaskDialogButton("Speichern");
        var discard = new TaskDialogButton("Nicht speichern");
        var choice = TaskDialog.ShowDialog(this, new TaskDialogPage
        {
            Caption = "MozillaPDF",
            Heading = $"Änderungen an „{Path.GetFileName(currentPath)}“ speichern?",
            Text = "Die Hervorhebungen und Anmerkungen gehen sonst verloren.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { save, discard, TaskDialogButton.Cancel },
            DefaultButton = save,
        });
        if (choice == save) { closeAfterSave = true; _ = webView.CoreWebView2.ExecuteScriptAsync("PDFViewerApplication.downloadOrSave()"); } // schließt nach dem Download
        else if (choice == discard) { closeApproved = true; Close(); }
        // Abbrechen: offen bleiben
    }

    /// <summary>Derselbe Test, mit dem PDF.js beim Schließen eines Dokuments selbst entscheidet, ob es speichern muss (close()).</summary>
    private async Task<bool> HasUnsavedChangesAsync()
    {
        try
        {
            return await webView.CoreWebView2.ExecuteScriptAsync(
                "(() => { const a = window.PDFViewerApplication; return !!(a && a._annotationStorageModified && a._hasChanges && a._hasChanges()); })()") == "true";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; } // Viewer schon weg
    }
}
