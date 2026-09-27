# Erzeugt SmartPDF-Hilfe.pdf aus SmartPDF-Hilfe.html: Edge druckt die Seite im Headless-Modus als PDF (A4 laut @page, ohne
# Kopf- und Fußzeilen des Browsers). Die PDF wird eingecheckt – der Build kopiert sie neben die EXE, Edge braucht es nur hier.
# Nach jeder Änderung an der HTML-Datei (oder am Icon, das sie als smartpdf-icon.svg einbindet) ausführen.
param(
    [string]$Html = (Join-Path $PSScriptRoot "SmartPDF-Hilfe.html"),
    [string]$Out = (Join-Path $PSScriptRoot "SmartPDF-Hilfe.pdf")
)
$ErrorActionPreference = 'Stop'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge nicht gefunden – er druckt die Hilfe als PDF." }
$work = Join-Path $env:TEMP "SmartPDF-help"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null
$tmp = Join-Path $work "help.pdf"
$url = "file:///" + (Resolve-Path $Html).Path.Replace('\', '/')
$args = @("--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--user-data-dir=`"$work\profile`"",
          "--no-pdf-header-footer", "--print-to-pdf=`"$tmp`"", "`"$url`"")
Start-Process $edge -ArgumentList $args -Wait -WindowStyle Hidden
if (-not (Test-Path $tmp) -or (Get-Item $tmp).Length -eq 0) { throw "Edge hat keine PDF geliefert." }
Copy-Item $tmp $Out -Force
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
"$Out ($([math]::Round((Get-Item $Out).Length / 1KB)) KB)"
