using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using SmartPDF.Classes;

namespace SmartPDF.Forms;

/// <summary>PDF-Betrachter mit Hervorheben und Anmerkungen auf Basis von PDF.js (Mozilla) im WebView2.
/// <list type="bullet">
/// <item>Der fertige Viewer der Distribution (pdfjs\web\viewer.html) läuft unter dem virtuellen Host https://pdfjs.local, ohne Datei
/// (<c>?file=</c> leer). Alles liegt lokal – PDF.js geht nicht ins Netz.</item>
/// <item>Die Datei geht als CoreWebView2SharedBuffer an die Seite; ein beim Dokumentstart eingefügtes Skript (<see cref="PageScript"/>)
/// übergibt sie mit <c>PDFViewerApplication.open({ data, filename })</c>. Keine Kopie auf der Platte – der Programmordner darf
/// schreibgeschützt sein (Installation unter „Programme“).</item>
/// <item>Keine eigene Symbolleiste und keine Statuszeile: Die Leiste von PDF.js bringt alles mit. Das Seitenskript leitet nur das
/// Öffnen (Knopf im Menü „»“, Strg+O) auf den Öffnen-Dialog der App um, weil die App den Pfad fürs Speichern kennen muss, und ergänzt
/// einen „?“-Knopf samt F1 für die Hilfe-PDF (<see cref="ShowHelp"/>) und einen „i“-Knopf für die Programminformationen
/// (<see cref="ShowAbout"/>). Fehler meldet ein Dialog.</item>
/// <item>Speichern (Speichern-Knopf der Leiste, Strg+S) bettet die Hervorhebungen ein und löst einen Download aus; das WebView fängt ihn
/// ab und schreibt ihn dorthin, wo der Speichern-Dialog es will. Beim Schließen und vor dem Öffnen einer anderen Datei fragt die App bei
/// ungespeicherten Änderungen nach (Speichern / Nicht speichern / Abbrechen); „Speichern“ löst den Download des Viewers aus und wartet
/// auf ihn (<see cref="SaveViaViewerAsync"/>). PDF.js selbst speichert beim Dokumentwechsel nicht mehr – das Seitenskript setzt vor dem
/// Öffnen den Änderungsmerker zurück, sonst käme der Speichern-Dialog ein zweites Mal, und zwar mit dem Namen der neuen Datei.</item>
/// <item>Die Browser-Tastenkürzel von WebView2 sind aus (F5/Strg+R luden die Viewer-Seite neu und warfen das Dokument samt Änderungen
/// weg); PDF.js behandelt Strg+F, Strg+P und den Zoom selbst per keydown.</item>
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
    private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartPDF");
    private readonly AppSettings settings = AppSettings.Load();
    private string? startFile;   // Datei aus der Befehlszeile, geöffnet sobald der Viewer steht
    private string? currentPath; // angezeigte Datei; Vorgabe für den Speichern-Dialog
    private bool pageReady;
    private bool closeApproved;  // Rückfrage beim Schließen erledigt: FormClosing lässt das nächste Schließen durch
    private TaskCompletionSource<bool>? saveTask; // ein von der App ausgelöstes Speichern (downloadOrSave) wartet auf seinen Download

    public MainForm(string[] args)
    {
        InitializeComponent();
        startFile = args.Where(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a)).Select(FullPath).FirstOrDefault(p => p != null);
        RestoreWindowBounds();
    }

    /// <summary>Voller Pfad für Befehlszeilenangaben – ein relativer bliebe sonst bis in den Speichern-Dialog relativ (ohne Startordner).</summary>
    private static string? FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { return null; }
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
        var maximized = WindowState == FormWindowState.Maximized;
        if (beforeFullScreen is { } before) { (bounds, maximized) = (before.Bounds, before.State == FormWindowState.Maximized); } // Schließen im Präsentationsmodus
        (settings.WindowX, settings.WindowY, settings.WindowWidth, settings.WindowHeight) = (bounds.X, bounds.Y, bounds.Width, bounds.Height);
        settings.WindowMaximized = maximized;
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
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // F5/Strg+R luden die Viewer-Seite neu – Dokument und Änderungen weg (Review 26.09.2026); PDF.js' eigene Kürzel bleiben
        core.SetVirtualHostNameToFolderMapping(Host, pdfjsFolder, CoreWebView2HostResourceAccessKind.DenyCors); // Viewer, Worker, Schriften, CMaps, Locale – nur vom eigenen Ursprung
        core.DownloadStarting += Core_DownloadStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.WebMessageReceived += Core_WebMessageReceived;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.NavigationStarting += Core_NavigationStarting;
        core.ContainsFullScreenElementChanged += Core_ContainsFullScreenElementChanged;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript);
        core.Navigate($"https://{Host}/web/viewer.html?file="); // leer: der Viewer startet ohne Dokument (sonst zeigt er sein Beispiel)
    }

    /// <summary>Läuft in der Viewer-Seite vor deren eigenen Skripten:
    /// <list type="bullet">
    /// <item>nimmt die Datei als SharedBuffer entgegen, übergibt sie an PDF.js und meldet „opened“ bzw. „error“;</item>
    /// <item>ersetzt die unsichtbare Dateiauswahl des Viewers (<c>_openFileInput</c>): Öffnen-Knopf im Menü „»“ und Strg+O rufen beide nur
    /// deren click() auf (onOpenFile, PDF.js 6.3) – statt des Browser-Dialogs kommt „openRequest“ an die App;</item>
    /// <item>setzt vor das Menü „»“ einen „?“- und einen „i“-Knopf im Stil der Leiste (Symbole als CSS-Maske wie die übrigen Knöpfe) und
    /// fängt F1 ab – „?“ und F1 melden „help“, „i“ meldet „about“;</item>
    /// <item>eigene Kürzel: Strg+H „Hervorheben“, Strg+T „Text“ und Strg+I „Dokumenteigenschaften“ als Umschalter, Strg+G springt ins
    /// Seitenfeld, F11 schaltet den Präsentationsmodus ein und aus; die Strg+Alt-Kürzel von PDF.js (P, G) sind gesperrt;</item>
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
          const hideHint = () => document.getElementById("spEmptyHint")?.remove();
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
              // Die App hat vor dem Wechsel selbst gefragt (gespeichert oder verworfen): den Änderungsmerker löschen, sonst speicherte
              // close() im open() noch einmal – der Download käme erst nach dem Wechsel an und träfe den Namen der neuen Datei
              viewer.pdfDocument?.annotationStorage?.resetModified();
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
              let tries = 0; // _openFileInput ist ein Internum von PDF.js – fehlt es nach einem Update, soll das auffallen statt still zu warten
              const wait = () => viewer._openFileInput ? resolve(true) : (++tries > 250 ? resolve(false) : setTimeout(wait, 20));
              wait();
            });
            if (!await input()) { post({ type: "error", message: "Der Öffnen-Knopf des Viewers lässt sich nicht umleiten (PDF.js-Version geändert?). Öffnen geht weiter per Drag & Drop." }); return; }
            viewer._openFileInput = { click: () => post({ type: "openRequest" }) }; // legt run() erst nach der Initialisierung an
          })();
          // Rahmen, solange Dateien über dem Fenster sind. Ein Zähler aus dragenter/dragleave taugt nicht: Chromium meldet schon beim
          // Hereinziehen ein dragleave, das ihn sofort ausgleicht (geprüft 25.09.2026). Deshalb: jedes dragover frischt den Rahmen auf,
          // er endet beim Ablegen, wenn der Zeiger das Fenster verlässt, oder eine Sekunde nach dem letzten dragover (Abbruch mit Esc
          // meldet kein dragleave; beim Stillhalten wiederholt Chromium dragover laufend, dort erlischt der Rahmen also nicht).
          let dragTimer = 0;
          const hasFiles = e => [...(e.dataTransfer?.types || [])].includes("Files");
          const endDrag = () => { clearTimeout(dragTimer); document.body.classList.remove("spDrop"); };
          window.addEventListener("dragover", e => {
            if (!hasFiles(e)) { return; }
            e.preventDefault(); e.stopImmediatePropagation();
            e.dataTransfer.dropEffect = "copy";
            document.body.classList.add("spDrop");
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
            // damit „Weitersuchen“ von PDF.js – das bleibt über Enter im Suchfeld erreichbar (Umschalt+Enter und Strg+Umschalt+G:
            // vorheriger Treffer). F3 kennt PDF.js nicht (geprüft 27.09.2026).
            const plainCtrl = e.ctrlKey && !e.altKey && !e.shiftKey && !e.metaKey;
            const tools = { h: "editorHighlightButton", t: "editorFreeTextButton" };
            const key = e.key.toLowerCase();
            // Strg+Alt-Kombinationen gehören dem System und globalen Tastenkürzeln, nicht einem einzelnen Programm (Wunsch vom 27.09.2026 –
            // auf Wilhelms Rechner startet Strg+Alt+P Photoshop Elements). Die beiden von PDF.js sind deshalb gesperrt: Strg+Alt+P
            // (Präsentationsmodus, jetzt F11) und Strg+Alt+G (Seitenfeld, das macht Strg+G).
            if (e.ctrlKey && e.altKey && !e.metaKey && (key === "p" || key === "g")) { e.preventDefault(); e.stopImmediatePropagation(); return; }
            if (e.key === "F11" && !e.ctrlKey && !e.altKey && !e.shiftKey && !e.metaKey) { // Präsentationsmodus im Vollbild, als Umschalter
              e.preventDefault(); e.stopImmediatePropagation();
              const viewer = window.PDFViewerApplication;
              if (document.fullscreenElement) { document.exitFullscreen(); } // auch Esc beendet ihn
              else if (hasDocument) { viewer?.requestPresentationMode(); } // die App vergrößert das Fenster (ContainsFullScreenElementChanged)
              return;
            }
            if (plainCtrl && (key in tools || key === "g" || key === "i")) {
              e.preventDefault(); e.stopImmediatePropagation();
              if (!hasDocument) { return; }
              if (key === "i") { // Strg+I: „Dokumenteigenschaften“ aus dem Menü „»“, ebenfalls als Umschalter
                const dialog = document.getElementById("documentPropertiesDialog");
                document.getElementById(dialog?.open ? "documentPropertiesClose" : "documentProperties")?.click();
              } else if (key in tools) {
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
            const svg = body => `url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Ccircle cx='8' cy='8' r='6.9' fill='none' stroke='black' stroke-width='1.2'/%3E${body}%3C/svg%3E")`;
            const helpIcon = svg("%3Cpath d='M6 6.3a2 2 0 1 1 2.9 1.8c-.6.3-.9.8-.9 1.4v.4' fill='none' stroke='black' stroke-width='1.3' stroke-linecap='round'/%3E%3Ccircle cx='8' cy='11.7' r='.85'/%3E");
            const infoIcon = svg("%3Ccircle cx='8' cy='4.9' r='.9'/%3E%3Cpath d='M8 7.3v4.4' fill='none' stroke='black' stroke-width='1.5' stroke-linecap='round'/%3E");
            // Die Content-Security-Policy des Viewers (style-src 'self') sperrt eingefügte <style>-Elemente; ein per Skript erzeugtes
            // Stylesheet (CSSOM) ist davon nicht betroffen, Daten-URLs für Bilder erlaubt sie (img-src data:).
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(`#spHelpButton::before { -webkit-mask-image: ${helpIcon}; mask-image: ${helpIcon}; }
              #spInfoButton::before { -webkit-mask-image: ${infoIcon}; mask-image: ${infoIcon}; }
              #spEmptyHint { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; text-align: center;
                pointer-events: none; font: message-box; font-size: 15px; line-height: 1.6; color: var(--main-color); opacity: .75; }
              body.spDrop::after { content: ""; position: fixed; inset: calc(var(--toolbar-height) + 8px) 8px 8px; z-index: 100000;
                border: 3px dashed #7aa7d4; border-radius: 6px; pointer-events: none; }`);
            document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
            if (!hasDocument) { enableOutput(false); }
            const toggle = document.getElementById("secondaryToolbarToggle");
            if (toggle) { // vor dem Menü „»“: „?“ (Hilfe-PDF, F1) und „i“ (Über SmartPDF)
              const button = (id, title, label, type) => {
                const b = document.createElement("button");
                b.id = id; b.className = "toolbarButton"; b.type = "button"; b.tabIndex = 0; b.title = title;
                b.innerHTML = `<span>${label}</span>`;
                b.addEventListener("click", () => post({ type }));
                toggle.parentNode.insertBefore(b, toggle);
              };
              button("spHelpButton", "Hilfe (F1)", "Hilfe", "help");
              button("spInfoButton", "Über SmartPDF", "Über SmartPDF", "about");
            }
            const container = document.getElementById("viewerContainer");
            if (container) {
              const hint = document.createElement("div");
              hint.id = "spEmptyHint";
              hint.innerHTML = "<div><b>Kein Dokument geöffnet</b><br>Öffnen mit Strg+O, im Menü » rechts oben oder per Drag &amp; Drop<br>Hilfe mit F1 oder ?, Programminformationen mit ⓘ</div>";
              container.parentNode.appendChild(hint);
            }
          });
        })();
        """;

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) { return; } // von Core_NavigationStarting abgebrochen (Weblink, file:) – kein Fehler
            ShowError("Viewer konnte nicht geladen werden", e.WebErrorStatus.ToString());
            return;
        }
        if (pageReady) { return; }
        pageReady = true;
        if (startFile != null) { var file = startFile; startFile = null; BeginInvoke(() => ShowFile(file)); }
    }

    // ==== Präsentationsmodus im Vollbild

    private (FormWindowState State, Rectangle Bounds)? beforeFullScreen; // Lage vor dem Vollbild, null = kein Vollbild

    /// <summary>Der Präsentationsmodus von PDF.js (F11, Menü „»“) fordert per Fullscreen-API Vollbild an; WebView2 füllt damit von sich aus
    /// nur sein eigenes Fenster. Deshalb wird das Hauptfenster randlos über den ganzen Bildschirm gelegt, auf dem es steht (Wunsch vom
    /// 27.09.2026), und beim Verlassen (Esc, F11) mit Rahmen, Lage und Maximiert-Zustand wiederhergestellt. Maximiert muss vorher auf
    /// Normal, sonst ließe Windows die Taskleiste frei.</summary>
    private void Core_ContainsFullScreenElementChanged(object? sender, object e)
    {
        if (webView.CoreWebView2.ContainsFullScreenElement)
        {
            if (beforeFullScreen != null) { return; }
            beforeFullScreen = (WindowState, WindowState == FormWindowState.Normal ? Bounds : RestoreBounds);
            var screen = Screen.FromControl(this).Bounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen;
        }
        else if (beforeFullScreen is { } before)
        {
            beforeFullScreen = null;
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = before.Bounds;
            WindowState = before.State == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }
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
        Text = Path.GetFileName(path) + " – SmartPDF";
    }

    /// <summary>Meldungen des Seitenskripts: opened, error, openRequest (Öffnen-Knopf oder Strg+O im Viewer), help („?“-Knopf oder F1), about („i“-Knopf).
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
            case "about": BeginInvoke(ShowAbout); break;
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
        if (dialog.ShowDialog(this) != DialogResult.OK) { e.Cancel = true; FinishSave(false); return; }
        e.ResultFilePath = dialog.FileName;
        e.DownloadOperation.StateChanged += (s, args) =>
        {
            if (e.DownloadOperation.State == CoreWebView2DownloadState.Completed)
            {
                currentPath = e.DownloadOperation.ResultFilePath; // „Speichern unter“: Titel und nächster Vorschlag folgen der neuen Datei
                Text = Path.GetFileName(currentPath) + " – SmartPDF";
                FinishSave(true);
            }
            else if (e.DownloadOperation.State == CoreWebView2DownloadState.Interrupted)
            {
                ShowError("Speichern fehlgeschlagen", e.DownloadOperation.InterruptReason.ToString());
                FinishSave(false);
            }
        };
    }

    /// <summary>Ein wartendes <see cref="SaveViaViewerAsync"/> freigeben – die Fortsetzung läuft dank RunContinuationsAsynchronously nicht im
    /// Download-Rückruf, sondern über die Nachrichtenschleife.</summary>
    private void FinishSave(bool saved)
    {
        var pending = saveTask;
        saveTask = null;
        pending?.TrySetResult(saved);
    }

    /// <summary>Speichern-Knopf des Viewers auslösen und auf den fertigen Download warten (bzw. auf den Abbruch im Dialog). Nur aufrufen,
    /// wenn es Änderungen gibt – sonst käme kein Download und die Aufgabe bliebe offen.</summary>
    private Task<bool> SaveViaViewerAsync()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        saveTask = pending;
        _ = webView.CoreWebView2.ExecuteScriptAsync("PDFViewerApplication.downloadOrSave()");
        return pending.Task;
    }

    private enum SaveChoice { Save, Discard, Cancel }

    /// <summary>Rückfrage bei ungespeicherten Hervorhebungen oder Anmerkungen – beim Schließen und vor dem Dokumentwechsel.</summary>
    private SaveChoice AskSaveChanges()
    {
        var save = new TaskDialogButton("Speichern");
        var discard = new TaskDialogButton("Nicht speichern");
        var choice = TaskDialog.ShowDialog(this, new TaskDialogPage
        {
            Caption = "SmartPDF",
            Heading = $"Änderungen an „{Path.GetFileName(currentPath)}“ speichern?",
            Text = "Die Hervorhebungen und Anmerkungen gehen sonst verloren.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { save, discard, TaskDialogButton.Cancel },
            DefaultButton = save,
        });
        return choice == save ? SaveChoice.Save : choice == discard ? SaveChoice.Discard : SaveChoice.Cancel;
    }

    /// <summary>Vor dem Öffnen einer anderen Datei: bei Änderungen fragen, auf Wunsch speichern. True = weiter (nichts offen, gespeichert
    /// oder verworfen). Der Dialog läuft per BeginInvoke, nie in der Fortsetzung nach ExecuteScriptAsync (siehe Klassenkommentar).</summary>
    private async Task<bool> ConfirmSwitchAsync()
    {
        if (currentPath == null || !await HasUnsavedChangesAsync()) { return true; }
        var asked = new TaskCompletionSource<SaveChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() => asked.SetResult(AskSaveChanges()));
        return await asked.Task switch
        {
            SaveChoice.Save => await SaveViaViewerAsync(),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>Abgelegte Dateien öffnen: die erste PDF in diesem Fenster, jede weitere in einem neuen SmartPDF-Fenster (ein Dokument je
    /// Fenster). Keine PDF dabei: Hinweis.</summary>
    private async void OpenDroppedFiles(IReadOnlyList<string> paths)
    {
        var pdfs = paths.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        if (pdfs.Count == 0) { ShowError("Keine PDF-Datei", "SmartPDF öffnet nur PDF-Dateien."); return; }
        foreach (var more in pdfs.Skip(1)) { StartNewWindow(more); }
        var first = pdfs[0];
        if (!pageReady) { if (startFile == null) { startFile = first; } else { StartNewWindow(first); } return; } // vor dem ersten Laden: die Startdatei bleibt
        if (await ConfirmSwitchAsync()) { ShowFile(first); }
    }

    private void StartNewWindow(string path)
    {
        try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { ArgumentList = { path } }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowError($"„{Path.GetFileName(path)}“ konnte nicht geöffnet werden", ex.Message); }
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

    // ==== Hilfe und Programminformationen

    /// <summary>Hilfedatei neben der EXE (Quelle SmartPDF-Hilfe.html, erzeugt von make-help.ps1).</summary>
    private static string HelpFile => Path.Combine(AppContext.BaseDirectory, "SmartPDF-Hilfe.pdf");

    private long lastHelpTicks;

    /// <summary>„?“ in der Viewer-Leiste oder F1: die Hilfe-PDF – ohne Dokument in diesem Fenster, sonst in einem neuen (das angezeigte
    /// Dokument bleibt ungestört; wie in PDFlight). F1 im Viewer kommt zweimal an (Seitenskript und HelpRequested) – der zweite Aufruf
    /// binnen einer Sekunde wird verworfen, sonst gingen zwei Fenster auf.</summary>
    private void ShowHelp()
    {
        var now = Environment.TickCount64;
        if (now - lastHelpTicks < 1000) { return; }
        lastHelpTicks = now;
        if (!File.Exists(HelpFile)) { ShowError("Hilfedatei nicht gefunden", HelpFile); return; }
        if (string.Equals(currentPath, HelpFile, StringComparison.OrdinalIgnoreCase)) { return; } // wird hier schon angezeigt
        if (!pageReady) { startFile ??= HelpFile; return; } // Viewer steht noch nicht: wie eine Startdatei
        if (currentPath == null) { ShowFile(HelpFile); } else { StartNewWindow(HelpFile); }
    }

    /// <summary>„i“ in der Viewer-Leiste: kurze Programmbeschreibung, Version, PDF.js-Version, Autor und Lizenz.</summary>
    private void ShowAbout()
    {
        if (aboutOpen) { return; }
        aboutOpen = true;
        try { ShowAboutDialog(); }
        finally { aboutOpen = false; }
        FocusViewer();
    }

    private bool aboutOpen;

    private void ShowAboutDialog()
    {
        var version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?";
        using var appIcon = Icon.ExtractIcon(Application.ExecutablePath, 0, LogicalToDeviceUnits(32));
        var page = new TaskDialogPage
        {
            Caption = "Über SmartPDF",
            Heading = "SmartPDF",
            Text = $"Ein einfacher PDF-Betrachter auf <a href=\"{PdfJsWebsite}\">PDF.js</a>-Basis."
                 + Environment.NewLine + Environment.NewLine
                 + "PDF.js wird im Firefox-Webbrowser verwendet." + Environment.NewLine
                 + "PDF.js arbeitet vollständig offline und verschickt" + Environment.NewLine
                 + "keine Daten. SmartPDF ist kein Mozilla-Produkt.",
            Icon = appIcon != null ? new TaskDialogIcon(appIcon) : TaskDialogIcon.Information,
            Footnote = new TaskDialogFootnote
            {
                Text = $"Version {version}   •   PDF.js {PdfJsVersion() ?? "?"}   •   © 2026 W. Happe" + Environment.NewLine
                     + "SmartPDF und PDF.js stehen unter der <a href=\"apache\">Apache-Lizenz 2.0</a>.",
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
    private void ShowError(string heading, string text)
    {
        if (IsDisposed || !IsHandleCreated) { return; } // z.B. ein unterbrochener Download nach dem Schließen – dann gibt es niemanden mehr zu warnen
        BeginInvoke(() => TaskDialog.ShowDialog(this, new TaskDialogPage
        {
            Caption = "SmartPDF",
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Error,
        }));
    }

    // ==== Aktionen

    private async void OpenFile()
    {
        if (!pageReady || !await ConfirmSwitchAsync()) { return; }
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
    /// Knopf des Viewers aus; das Fenster schließt erst, wenn der Download fertig ist (<see cref="SaveViaViewerAsync"/>).</summary>
    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // Windows fährt herunter oder meldet ab: nicht blockieren – die Rückfrage käme zu spät, und der Viewer könnte ohnehin nicht mehr speichern
        if (closeApproved || !pageReady || webView.CoreWebView2 == null || e.CloseReason == CloseReason.WindowsShutDown) { SaveWindowBounds(); return; }
        e.Cancel = true;
        var unsaved = await HasUnsavedChangesAsync();
        BeginInvoke(() => AskBeforeClose(unsaved)); // Dialog über die Nachrichtenschleife (siehe Klassenkommentar)
    }

    private async void AskBeforeClose(bool unsaved)
    {
        var choice = unsaved ? AskSaveChanges() : SaveChoice.Discard;
        if (choice == SaveChoice.Cancel) { return; } // offen bleiben
        if (choice == SaveChoice.Save && !await SaveViaViewerAsync()) { return; } // Speichern-Dialog abgebrochen oder fehlgeschlagen: offen bleiben
        closeApproved = true;
        Close();
    }

    /// <summary>Derselbe Test, mit dem PDF.js beim Schließen eines Dokuments selbst entscheidet, ob es speichern muss (close()). Die Felder
    /// sind PDF.js-Interna; fehlen sie nach einem Update, gilt ersatzweise „Anmerkungsspeicher nicht leer“ – lieber einmal zu viel fragen als
    /// Änderungen still verlieren. Die Fortsetzung wird bewusst über die Nachrichtenschleife geführt (Task.Yield), damit kein Aufrufer aus
    /// Versehen einen Dialog im Rückruf von ExecuteScriptAsync zeigt.</summary>
    private async Task<bool> HasUnsavedChangesAsync()
    {
        try
        {
            var result = await webView.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                  const a = window.PDFViewerApplication;
                  if (!a) { return false; }
                  if (typeof a._hasChanges === "function") { return !!(a._annotationStorageModified && a._hasChanges()); }
                  const storage = a.pdfDocument && a.pdfDocument.annotationStorage;
                  return !!(storage && storage.size > 0);
                })()
                """);
            await Task.Yield();
            return result == "true";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; } // Viewer schon weg
    }
}
