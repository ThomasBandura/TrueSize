# TrueSize.ScanHelper (Phase 5)

Der Helper führt Scans mit Adminrechten aus, während die App selbst nicht elevated läuft (Drag & Drop aus dem Explorer).
Code: `src/TrueSize.Core/Remote/` (Server, Client, Protokoll, Serialisierung), `src/TrueSize.ScanHelper/` (nur Einstieg).

## Ablauf

1. Die App erzeugt einen zufälligen Pipe-Namen `TrueSize-<32 hex>` und startet
   `TrueSize.ScanHelper.exe --pipe <name> --client-pid <eigene PID>`:
   - nicht elevated: per `runas` (ein UAC-Prompt pro Session),
   - bereits elevated (z. B. PowerShell als Admin): direkt; der Helper erbt die Elevation, kein Prompt.
2. Der Helper legt die Pipe an, wartet max. 30 s auf die Verbindung und sendet `Hello`.
3. Pro Scan: `ScanRequest` → `Progress` (alle 250 ms) → `Result` (Binärbaum) oder `Error`. `Cancel` bricht ab.
4. Eine Session kann mehrere Scans ausführen; `Bye`, Verbindungsende oder 15 min Leerlauf beenden den Helper.

## Sicherheitsmodell

Der Helper liest die rohe Platte und sieht Dateinamen aller Benutzer. Er darf deshalb nur dem Prozess antworten, der ihn
gestartet hat:

| Maßnahme | Wirkung | Test |
|---|---|---|
| Zufälliger Name, `FirstPipeInstance` | Vorab angelegte (gekaperte) Pipe → Helper bricht ab | `Helper_RefusesSquattedPipeName` |
| DACL nur für die Benutzer-SID, Netzwerk-SID verweigert, 1 Instanz | Andere Benutzer und Remote-Zugriffe ausgeschlossen | (manuell: runas-Test über Integritätsstufen) |
| Client-PID-Prüfung (`GetNamedPipeClientProcessId`) | Nur der startende Prozess darf sprechen | `Helper_RejectsClientWithWrongProcessId` |
| Server-PID-Prüfung (`GetNamedPipeServerProcessId`) | Die App spricht nur mit dem gestarteten Helper | im Client bei jedem Start |
| Whitelist: `ScanRequest`, `Cancel`, `Bye` | Keine Befehle, keine beliebigen Aktionen | `Helper_EndsSessionOnUnexpectedMessage` |
| Pfadprüfung: voll qualifiziert, lokal, Laufwerkstyp *Fixed* | Keine UNC-, Geräte- oder relativen Pfade | `ValidatePath_RejectsNonLocalOrInvalidPaths` |
| Größenlimit 64 KB je Nachricht, strikte Argumente | Kein Speicherangriff über die Pipe | `Protocol_RejectsOversized…`, `Arguments_AreValidatedStrictly` |
| Leerlauf-Timeout | Kein dauerhaft wartender Admin-Prozess | `Helper_ExitsAfterIdleTimeout` |
| Empfänger validiert den Baum (Indizes, Eltern vor Kindern) | Defekte Daten führen zu Fehler statt Fehlberechnung | `Serializer_RejectsChildBeforeParent` |

Grenze: Ein Schadprozess desselben Benutzers könnte den Helper selbst starten, braucht dafür aber die Zustimmung im
UAC-Dialog.

## Protokoll

Rahmen: `[byte Typ][int32 Länge][Nutzdaten]`, Little Endian. `Result` hat Länge 0 und wird von einem selbstbegrenzenden
Binärblock gefolgt (`ScanTreeSerializer`): Struct-of-Arrays als Rohbytes in 1-MB-Blöcken, Reparse-Tags und Fehler als
Blöcke. Aggregate werden nicht übertragen, der Empfänger berechnet sie neu.

Wichtig für die Geschwindigkeit: Die Pipe ist für Overlapped-I/O geöffnet (Abbrechen während des Lesens). Dort ist jeder
Lese-/Schreibaufruf eine eigene I/O-Operation und kopiert über einen Puffer in Aufrufgröße. Kleine Einzelzugriffe pro
Knoten oder sehr große Einzelzugriffe kosteten anfangs 85 s für `C:`; jetzt ~1 s Übertragung für 4,6 Mio. Knoten.

## Messwerte (`C:`, 4,6 Mio. Knoten, elevated)

| Variante | Gesamt |
|---|---|
| MFT-Scanner direkt | 5,0 s |
| Helper (JIT) inkl. Übertragung | 6,4 s |
| Helper (NativeAOT, 2,1 MB, ohne .NET-Runtime) inkl. Übertragung | 6,1 s |
| `Get-TrueSize C:\` in der Store-PowerShell (automatisch über den Helper) | 11 s (vorher 186 s Fallback) |

## Veröffentlichen

```powershell
# NativeAOT (benötigt die C++-Build-Tools; vswhere muss im PATH sein)
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet publish src/TrueSize.ScanHelper -r win-x64 -c Release
```

Das PowerShell-Modul liefert den (framework-abhängigen) Helper im Modulordner mit; er wird nur genutzt, wenn rohe
Volume-Zugriffe im PowerShell-Prozess selbst blockiert sind (Store-Version von PowerShell).
