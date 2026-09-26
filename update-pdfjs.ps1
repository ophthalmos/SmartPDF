<#
.SYNOPSIS
  Prüft, ob es eine neuere PDF.js-Version gibt, und ersetzt in dem Fall den Ordner pdfjs durch die neue Distribution.

.DESCRIPTION
  Die installierte Version steht in pdfjs\build\pdf.mjs (pdfjsVersion). Die neueste kommt aus dem letzten regulären Release von
  github.com/mozilla/pdf.js (keine Vorabversionen), Archiv pdfjs-<version>-dist.zip (nicht „legacy“). Der Download wird gegen die
  SHA-256-Prüfsumme geprüft, die GitHub zum Archiv angibt, entpackt und auf web\viewer.html und build\pdf.mjs kontrolliert. Erst dann
  wird der alte Ordner ersetzt – bei jedem Fehler bleibt er unverändert.
  Danach das Programm neu bauen (dotnet build), damit der neue Ordner neben die EXE kommt, und ggf. den Installer neu erzeugen.

.PARAMETER CheckOnly
  Nur prüfen und melden, nichts herunterladen.

.PARAMETER Force
  Auch bei gleicher Version neu herunterladen und ersetzen.

.PARAMETER ZipFile
  Statt des Downloads ein bereits vorhandenes dist-Archiv verwenden (z. B. von Hand geladen).

.PARAMETER Target
  Zielordner (Vorgabe: pdfjs neben diesem Skript).

.EXAMPLE
  .\update-pdfjs.ps1 -CheckOnly

.NOTES
  Rückgabewert (Exitcode): 0 = aktuell bzw. aktualisiert, 2 = neuere Version verfügbar (nur bei -CheckOnly), 1 = Fehler.
#>
param(
    [switch]$CheckOnly,
    [switch]$Force,
    [string]$ZipFile = "",
    [string]$Target = (Join-Path $PSScriptRoot 'pdfjs')
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-InstalledVersion([string]$folder) {
    $mjs = Join-Path $folder 'build\pdf.mjs'
    if (-not (Test-Path $mjs)) { return $null }
    $head = Get-Content $mjs -TotalCount 200 -Encoding UTF8 | Out-String
    # Kopfkommentar „ * pdfjsVersion = 6.3.289“ (ohne Anführungszeichen); ältere Ausgaben schrieben const pdfjsVersion = "x.y.z"
    $pattern = 'pdfjsVersion\s*=\s*"?([0-9]+(?:\.[0-9]+)+)'
    $m = [regex]::Match($head, $pattern)
    if ($m.Success) { return [version]$m.Groups[1].Value }
    $m = [regex]::Match((Get-Content $mjs -Raw -Encoding UTF8), $pattern) # falls weiter unten
    if ($m.Success) { return [version]$m.Groups[1].Value } else { return $null }
}

try {
    $installed = Get-InstalledVersion $Target
    "Installiert: " + $(if ($installed) { "PDF.js $installed" } else { "keine PDF.js-Distribution in $Target" })

    if ($ZipFile) {
        if (-not (Test-Path $ZipFile)) { throw "Archiv nicht gefunden: $ZipFile" }
        $zip = (Resolve-Path $ZipFile).Path; $latest = $null; $digest = $null; $ownZip = $false
    } else {
        $release = Invoke-RestMethod 'https://api.github.com/repos/mozilla/pdf.js/releases/latest' -Headers @{ 'User-Agent' = 'MoziPDF-update'; 'Accept' = 'application/vnd.github+json' }
        $latest = [version]($release.tag_name.TrimStart('v'))
        $asset = $release.assets | Where-Object { $_.name -like 'pdfjs-*-dist.zip' -and $_.name -notlike '*legacy*' } | Select-Object -First 1
        if (-not $asset) { throw "Im Release $($release.tag_name) gibt es kein dist-Archiv." }
        "Neueste:     PDF.js $latest ($($asset.name), $([math]::Round($asset.size / 1MB, 1)) MB, veröffentlicht $(([datetime]$release.published_at).ToString('dd.MM.yyyy')))"
        if ($installed -and $installed -ge $latest -and -not $Force) { "PDF.js ist aktuell."; exit 0 }
        if ($CheckOnly) { "Eine neuere Version ist verfügbar. Zum Aktualisieren ohne -CheckOnly aufrufen."; exit 2 }
        $zip = Join-Path $env:TEMP $asset.name; $digest = $asset.digest; $ownZip = $true
        "Lade herunter …"
        Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
        if ((Get-Item $zip).Length -ne $asset.size) { throw "Download unvollständig ($((Get-Item $zip).Length) statt $($asset.size) Byte)." }
    }

    if ($digest -and $digest -like 'sha256:*') {
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $digest.Substring(7).ToLowerInvariant()) { throw "Prüfsumme stimmt nicht – Archiv verworfen." }
        "Prüfsumme (SHA-256) stimmt."
    } elseif (-not $ZipFile) { "Hinweis: GitHub nennt keine Prüfsumme – nur die Größe wurde geprüft." }

    # In einen Nachbarordner entpacken und prüfen, erst dann austauschen
    $staging = "$Target.neu"; $backup = "$Target.alt"
    foreach ($dir in $staging, $backup) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
    Expand-Archive $zip -DestinationPath $staging
    foreach ($needed in 'web\viewer.html', 'build\pdf.mjs') {
        if (-not (Test-Path (Join-Path $staging $needed))) { Remove-Item $staging -Recurse -Force; throw "Archiv ohne $needed – keine gültige PDF.js-Distribution." }
    }
    $new = Get-InstalledVersion $staging
    if (Test-Path $Target) { Move-Item $Target $backup }
    try { Move-Item $staging $Target }
    catch { if (Test-Path $backup) { Move-Item $backup $Target }; throw }  # alten Stand zurück
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    if ($ownZip) { Remove-Item $zip -ErrorAction SilentlyContinue }
    "PDF.js $new nach $Target übernommen. Jetzt neu bauen (dotnet build) und ggf. den Installer neu erzeugen."
    exit 0
}
catch {
    Write-Host ("Fehler: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
