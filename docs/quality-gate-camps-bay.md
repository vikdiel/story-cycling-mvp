# Quality Gate — Küstenstadt-Strecke (Camps Bay Promenade Drive)

Referenz-Maßstab für „eine Strecke sieht vollständig aus". Jede Zeile ist ein abhakbarer
Check. Erst wenn alle Kategorien grün sind, gilt die Iteration als fertig.

Grundprinzip: **Dichte + Verteilung schlagen Einzelobjekt.** Ein sauberer Baum nützt nichts,
wenn pro Kilometer nur 10 stehen. Deshalb sind die Punkte wo sinnvoll mit Mindestmengen
pro 100 m Strecke formuliert („Dichte").

---

## 1. Straßennetz & Verkehrslogik
- [ ] Fahrbahn + Bürgersteig/Kante durchgängig, keine Lücken im Belag
- [ ] Querstraßen/Einmündungen an sinnvollen Punkten (nicht zufällig), mit Zebrastreifen
- [ ] Ampeln **oder** Vorfahrt-Schilder an jeder Einmündung (konsistent, nicht gemischt)
- [ ] Straßennamenschilder an jeder Kreuzung
- [ ] Kein Objekt ragt in den Fahrkorridor (10 m frei)
- [ ] Kurven/Abzweige führen visuell irgendwohin (keine „tote" Straße, die ins Nichts endet)

## 2. Bebauung & Fassaden
- [ ] Dichte: Gebäude an ~60–80 % der Straßenfront (keine langen leeren Wände)
- [ ] Mind. 4–6 **unterschiedliche** Gebäudevarianten sichtbar (keine Klon-Reihe)
- [ ] Fassaden variieren in Farbe/Höhe; keine zwei identischen direkt nebeneinander
- [ ] Gebäude bodenverankert (keine schwebenden Fundamente)
- [ ] Erdgeschoss-/Ladenfront zeigt zur Straße (nicht die Rückwand zum Fahrer)
- [ ] Dächer/Details (Markisen, Fenster, Balkone) vorhanden, keine „Kisten"

## 3. Vegetation & Biotop
- [ ] Dichte: Vegetation auf **mind. 50 %** der nicht bebauten Fläche (nicht vereinzelt)
- [ ] Mix aus Bäumen, Büschen, Gras, Blumen — nicht nur eine Pflanzenart
- [ ] Palmen entlang der Strandseite (Promenade) in Reihe
- [ ] Bäume/Pflanzen bodenverankert, keine in der Luft
- [ ] Pflanzen passen zum Motiv (Küstenstadt: Palmen, Fynbos, Büsche — kein Nadelwald)

## 4. Leben & Figuren
- [ ] Fußgänger/Figuren entlang der Promenade (nicht statisch, min. ~1 pro 50 m)
- [ ] Sitzende/stehende Figuren an Bänken, Cafés, Haltestellen
- [ ] Parkende Autos am Straßenrand (min. ~1 pro 100 m im bebauten Abschnitt)
- [ ] Figuren im Maßstab passend (nicht Riesen/Zwerge relativ zu Gebäuden)

## 5. Straßenmöblierung & Deko
- [ ] Laternen entlang der Strecke + an jeder Kreuzung, bodenverankert
- [ ] Bänke, Mülleimer, Hydranten, Parkuhren verteilt (kein kahler Bürgersteig)
- [ ] Schilder: Vorfahrt, Stop, Warnung, Parken — konsistent platziert
- [ ] Klein-Deko (Kisten, Plakate, Markisen) bricht lange Flächen auf

## 6. Szenen & Ereignisse („Story-Momente")
- [ ] Min. 3–4 markante Szenen pro Strecke verteilt (nicht gehäuft an einer Stelle)
- [ ] Baustelle: Absperrung + Kegel + Container + Warnschild
- [ ] Polizei: Streifenwagen + Beamter
- [ ] Unfall: zwei Autos + Kegel + ggf. Krankenwagen
- [ ] Szenen liegen seitlich der Fahrbahn, blockieren den Fahrer nicht

## 7. Landschaft & Umgebung (Küstenstadt)
- [ ] Meer/Ozean + Strand auf der Seeseite sichtbar und durchgängig
- [ ] Berg/Silhouette als Hintergrund-Kulisse auf der Landseite
- [ ] Strand mit Deko (Liegen, Schirme, Felsen) — nicht leerer Sand
- [ ] Himmel/Licht/Stimmung passend (goldene Stunde, Nebel)
- [ ] Wolken/Himmel-Deko vorhanden (nicht leeres Blau)

## 8. Hintergrund & Tiefenstaffelung (kein Blick ins Leere)
- [ ] Hinter der ersten Häuserreihe stehen weitere Häuser — keine Lücke zum Horizont
- [ ] Größere Villen/Wohnblöcke als zweite Reihe hinter der Ladenfront
- [ ] Auch entlang der Rückseite (abgewandte Straßenseite) bebaut — kein kahles Ende
- [ ] Blickt der Fahrer zwischen zwei Häusern hindurch, sieht er dahinter Stadt/Land, nicht Vakuum
- [ ] Ferne Staffelung: Stadt-Silhouette/Bäume/Berg füllen den Horizont, kein abruptes Nichts

## 9. Technische Qualität
- [ ] Build ohne Fehler, QA-PASS (Route + Cyclist + Platzierung)
- [ ] Kein Objekt schwebt oder versinkt (Bodenhaftung via Bounds)
- [ ] Keine Überlappung im Fahrkorridor
- [ ] Performance ok auf iPad (Instancing/Batching genutzt, keine Tausenden Einzel-Drawcalls)
- [ ] Determinismus: gleicher Seed → gleiche Strecke (reproduzierbar)

---

## Ablauf der nächsten Camps-Bay-Iteration
1. Bestandsaufnahme: aktuelle Strecke gegen diese Liste prüfen (welche Kategorien rot).
2. Priorität: **Vegetation-Dichte (3)** und **Leben (4)** zuerst — das ist der größte sichtbare Hebel.
3. Danach Szenen (6) und Straßenmöblierung (5).
4. Jede Änderung: bauen → QA-PASS → **ein** Stichproben-Screenshot, Rest prüft Vik auf iPad.
