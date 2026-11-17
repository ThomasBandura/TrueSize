# TrueSize

> [!CAUTION]
> ## ⚠️ KI-generierter Code – vor Verwendung prüfen
>
> **Der Code dieses Projekts wurde vollständig von einer KI generiert** (Claude Code).
>
> - ❗ **Vor dem Kompilieren und Ausführen selbst prüfen.** Das gilt besonders für `TrueSize.ScanHelper`: Er läuft
>   mit Adminrechten und liest das Volume roh.
> - ❗ Die Werkzeuge lesen nur und löschen oder ändern keine Dateien. Trotzdem können Größenangaben falsch sein –
>   **Entscheidungen zum Löschen nicht allein auf diese Werte stützen.**
> - ❗ **Keine Haftung, keine Gewährleistung.** Nutzung auf eigenes Risiko; es gilt die [MIT-Lizenz](LICENSE).

---

Analyse der Speicherbelegung lokaler Laufwerke (NTFS und ReFS) unter Windows – mit korrekt berechneten Größen:

- **Hardlinks werden einmal gezählt** (wählbar: erster Fund, anteilig oder separat ausgewiesen).
- **Junctions, symbolische Links und Bereitstellungspunkte werden nicht verfolgt**, sondern markiert.
- **Größe, Belegt und Freigebbar getrennt**: logische Größe, tatsächlich belegter Platz (Sparse, NTFS-Kompression,
  WOF/CompactOS) und der Platz, der beim Löschen wirklich frei würde – Dateien mit Hardlinks außerhalb des Ordners
  zählen dort nicht.
- **Cloud-Platzhalter** (OneDrive) werden nie geöffnet, also auch nie heruntergeladen.
- **ReFS Block Cloning** wird nicht geraten: Geteilter Platz wird nur ausgewiesen, wo er sich nachweisen lässt.

Beispiel `C:\Windows`: *System32* belegt 13,8 GB, freigebbar sind nur 9,9 GB – der Rest sind Hardlinks nach *WinSxS*.

## Komponenten

| Komponente | Zweck |
|---|---|
| `TrueSize.App` | WinUI-3-Oberfläche, läuft ohne Adminrechte (Drag & Drop aus dem Explorer) |
| `TrueSize.PowerShell` | Binärmodul mit `Get-TrueSize` für PowerShell 7.6+ |
| `TrueSize.ScanHelper` | Erhöht laufender Hilfsprozess für den schnellen Scan, Ergebnis per abgesicherter Named Pipe |
| `TrueSize.Core` | Scanner, Datenmodell, Aggregation, Interop (trim- und NativeAOT-kompatibel) |

## Scanner

| Scanner | Wann | Geschwindigkeit (Beispiel `C:`, 3,7 Mio. Dateien) |
|---|---|---|
| `NtfsMftScanner` | NTFS mit Adminrechten: liest die MFT direkt vom Volume | ~5 s |
| `DirectoryScanner` | ohne Adminrechte und auf ReFS: parallele Verzeichnis-Enumeration | 94–185 s |

Auf ReFS gibt es keinen schnelleren Weg (`FSCTL_ENUM_USN_DATA` wird dort nicht unterstützt, siehe
[docs/refs.md](docs/refs.md)). Jeder schnelle Scanner wird in Tests Knoten für Knoten gegen einen langsamen
Referenz-Scanner geprüft.

## Voraussetzungen

- Windows 10 2004 oder neuer (empfohlen Windows 11 24H2, dort werden Hardlink-Zähler ohne Öffnen der Datei gelesen)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) zum Bauen
- PowerShell 7.6 oder neuer für das Modul
- Für den schnellen Scan: Administratorkonto (UAC-Bestätigung)

## Bauen und testen

```powershell
dotnet build
dotnet test tests/TrueSize.Core.Tests
```

Tests, die Adminrechte brauchen (MFT-Vergleich auf einer temporären VHDX, Helper elevated), werden ohne Adminrechte
übersprungen. PowerShell-Tests: `Invoke-Pester ./tests/TrueSize.PowerShell.Tests` (Pester 5).

## Verwendung

### App

```powershell
dotnet run --project src/TrueSize.App -p:Platform=x64
```

Ordner hineinziehen, auswählen oder Pfad eingeben. Mit „Schnell (Admin)“ fragt Windows einmal pro Sitzung nach
Adminrechten; gescannt wird dann im Helper, die App selbst bleibt ohne Adminrechte. Spaltenüberschriften sortieren,
Doppelklick oder Pfeil klappt Ordner auf, Rechtsklick öffnet „Im Explorer anzeigen“ und „Pfad kopieren“.

### PowerShell

```powershell
Import-Module ./src/TrueSize.PowerShell/bin/Debug/net10.0-windows/TrueSize.psd1

Get-TrueSize C:\Users -Top 10
Get-TrueSize D:\Projekte -AsTree -Depth 2 -MinSize 100MB
Get-TrueSize C:\ -HardlinkMode Separate
```

In einer PowerShell mit Adminrechten wird auf NTFS automatisch der MFT-Scanner genutzt. Hinweis: In der
**Microsoft-Store-Version von PowerShell** sind direkte Volume-Zugriffe im PowerShell-Prozess blockiert; das Modul
startet dafür automatisch den mitgelieferten Helper als Kindprozess.

## Sicherheit

Der Helper liest das Volume roh und sieht damit Dateinamen aller Benutzer. Er antwortet deshalb nur dem Prozess, der
ihn gestartet hat (zufälliger Pipe-Name, ACL nur für den eigenen Benutzer, Prüfung der Prozess-IDs, nur Scan-Anfragen
für lokale Laufwerke, Leerlauf-Timeout). Details: [docs/scanhelper.md](docs/scanhelper.md).

## Stand und Grenzen

- Ein Release-Paket (App mit NativeAOT-Helper) ist noch nicht eingerichtet; der Helper lässt sich mit
  `dotnet publish src/TrueSize.ScanHelper -r win-x64 -c Release` als NativeAOT-Einzeldatei erzeugen.
- Einzelne Werte im MFT-Format sind nicht von Microsoft dokumentiert (Belegung residenter Daten, Reparse-Tag-Kopie in
  `$FILE_NAME`); sie sind im Code gekennzeichnet und durch Vergleichstests abgesichert.
- Ohne Adminrechte fehlen geschützte Ordner (z. B. `System Volume Information`); sie werden als Zugriffsfehler gemeldet.

## Lizenz

[MIT](LICENSE) – die Software wird ohne jede Gewährleistung bereitgestellt („as is“), die Autoren haften nicht für
Schäden aus ihrer Nutzung.
