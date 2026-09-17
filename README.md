# Story Cycling MVP

Eine iPad-only Story-Cycling-App: Ein Winter in Südafrika, fünf fiktive Qualifikationsrennen und echte Trainingswerte vom Wahoo KICKR Core.

## Status

Der erste App-Shell-Build enthält eine spielbare Landschafts-Startszene für **Kapitel 1: Ankunft**. Bluetooth, Trainer-Auswahl und Fahrtwerte folgen als getrennte technische Schritte, damit wir den KICKR Core auf echter Hardware validieren können.

## Voraussetzungen

- macOS mit Xcode 16 oder neuer
- iPadOS 17 oder neuer auf dem Test-iPad
- Für den Trainer-Test: ein eingeschalteter Wahoo KICKR Core, der nicht gleichzeitig mit Zwift oder Wahoo SYSTM verbunden ist

## Lokal starten

1. Repository klonen und `StoryCycling.xcodeproj` in Xcode öffnen.
2. Als Ziel ein iPad-Simulator oder dein per Kabel/WLAN verbundenes iPad wählen.
3. Für ein persönliches Gerät unter **Signing & Capabilities** dein Apple-Entwicklerteam wählen.
4. `Cmd + R` drücken.

Die App ist absichtlich auf **Landscape iPad** beschränkt. Der Bundle Identifier lautet aktuell `com.vikdiel.storycycling` und muss vor TestFlight eindeutig bleiben.

## CI

Jeder Push auf `main` oder einen `ralph/*`-Branch passiert drei Stufen: **Preflight** (Projektdatei, Metadaten und Pfade), unsignierter iPad-Simulator-**Build samt Unit-Test**, anschließend das zusammenfassende **Quality Gate**. Der Test wählt automatisch ein verfügbares iPad-Simulator-Modell, damit ein Xcode-Update die Pipeline nicht an einem Modellnamen zerlegt. Ein echter iPad-/Bluetooth-Test bleibt lokal oder läuft später über TestFlight.

## Nächste Schritte

1. BLE-Scan und sichere Verbindung zum KICKR Core
2. FTMS-Werte (Leistung, Kadenz, Geschwindigkeit) lesen und darstellen
3. Erste fahrbare 2.5D-Route an die realen Werte koppeln
