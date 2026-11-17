# ReFS: Prüfung eines schnellen Scanners (Phase 4)

Stand: 17.11.2026, Windows 11 Build 26300, ReFS 3.14.

## Frage

Kann ein `UsnEnumScanner` (`FSCTL_ENUM_USN_DATA` mit `MFT_ENUM_DATA_V1` / `USN_RECORD_V3`) auf ReFS schneller sein als der
`DirectoryScanner`?

## Dokumentation

- `FSCTL_ENUM_USN_DATA`: „The volume must be NTFS.“ ReFS wird nicht erwähnt.
- `MFT_ENUM_DATA_V1`: beschrieben als Struktur „for ReFS volumes“.
- `USN_RECORD_V3`: enthält **keine Dateigröße**. Größen müssten pro Datei nachgeladen werden (`OpenFileById` mit
  `ExtendedFileIdType` + `FileStandardInfo`; laut Doku auf ReFS unterstützt).

Die beiden Seiten widersprechen sich, daher empirische Prüfung.

## Messungen (elevated)

| Test | Ergebnis |
|---|---|
| `D:` (ReFS 3.14, Dev Drive), USN-Journal **nicht aktiv** | `FSCTL_ENUM_USN_DATA` → `ERROR_INVALID_FUNCTION` (1) |
| Temporäre ReFS-Dev-Drive-VHDX, Journal **nicht aktiv** | `ERROR_INVALID_FUNCTION` (1) |
| Dieselbe VHDX nach `fsutil usn createjournal` (Record-Versionen 2–3 unterstützt) | `ERROR_INVALID_FUNCTION` (1) |
| Gegenprobe: derselbe Code auf `C:` (NTFS) | OK, 4.125.636 Records (V3), **59,6 s** |
| Zum Vergleich: `NtfsMftScanner` auf `C:` | 4,8 s |
| `DirectoryScanner` auf `D:` (22.234 Dateien, 3.962 Verzeichnisse) | 0,40 s mit Linkanzahl, 0,03 s ohne |

## Entscheidung

- **Kein `UsnEnumScanner`.** ReFS unterstützt `FSCTL_ENUM_USN_DATA` auf diesem System nicht, auch nicht mit aktivem
  Journal. Selbst wenn es funktionierte, wäre „Aufzählung + `OpenFileById` je Datei“ grundsätzlich langsamer als der
  `DirectoryScanner`, der Größen pro Verzeichnis-Batch ohne Öffnen der Dateien erhält; auf NTFS ist schon die reine
  Aufzählung 12× langsamer als der MFT-Scan.
- Für ReFS ist der `DirectoryScanner` der einzige Scanner, elevated und nicht elevated. Elevated aktiviert er
  `SeBackupPrivilege` und sieht damit auch geschützte Verzeichnisse.
- `Get-TrueSize` warnt auf ReFS deshalb nicht mehr „nicht elevated“ (Elevation würde nichts beschleunigen).
- Ein Parser für das ReFS-On-Disk-Format kommt nicht in Frage: Es ist nicht dokumentiert.

## Block Cloning / Deduplizierung

ReFS kann Cluster zwischen Dateien teilen (Dev Drives klonen beim Kopieren standardmäßig). Pro Datei ist das nicht
günstig erkennbar, daher wird **nicht geraten**:

- „Belegt“ zählt geteilte Cluster pro Datei; die Summe kann die tatsächliche Belegung übersteigen.
- `ScanTree.MinimumSharedAllocation`: Nur beim Scan eines **ganzen** ReFS-Volumes und nur wenn die Summe „Belegt“ den
  tatsächlich belegten Platz (`GetDiskFreeSpaceEx`) übersteigt, wird die Differenz als „mindestens geteilt“ ausgewiesen.
  0 heißt nicht „nichts geteilt“ (Metadaten können es verdecken). `Get-TrueSize` gibt dann eine Warnung aus.
- „Freigebbar“ kann bei geklonten Dateien zu hoch sein; das wird im Cmdlet-Hinweis genannt.

## Erneut prüfen

Der Prüfcode liegt im Benchmark-Tool (nicht in Core):

```powershell
# elevated
.\tests\TrueSize.Benchmarks\bin\Release\net10.0-windows\TrueSize.Benchmarks.exe D:\ --usn-probe
.\tests\TrueSize.Benchmarks\bin\Release\net10.0-windows\TrueSize.Benchmarks.exe C:\ --usn-probe --enum-only
```

Gibt die erste Zeile auf ReFS `error=0` aus, misst das Tool automatisch das Nachladen per `OpenFileById` und vergleicht
mit dem `DirectoryScanner`.
