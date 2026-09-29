# osm2streets-Konvertierung (optional)

Standardmäßig AUS (`useOsm2StreetsJunctions` in der Route-Config). Das eigene Spurmodell in `RoadNet`
(Spuren, Richtungen, Breiten und Lage aus den OSM-Tags) liefert gleichwertige Kreuzungen; osm2streets
0.1.4 erkennt bei Kapstadt den Linksverkehr nicht und ignoriert `width`.

Erzeugt aus der OSM-Datei einer Strecke robuste Fahrbahn- und Kreuzungsflächen mit
[osm2streets](https://github.com/a-b-street/osm2streets) (Apache-2.0), statt sie mit
unserem eigenen Flächen-Code selbst zu konstruieren. osm2streets ist ein von der
OpenStreetMap-Community entwickeltes, gepflegtes Projekt, das genau die Fälle löst, an
denen ein selbstgebauter Algorithmus zuverlässig scheitert: Doppelfahrbahnen, versetzte
"Dog-Leg"-Kreuzungen, Kreisverkehre mit Bypass-Spuren.

Verwendet werden nur die **Kreuzungsflächen** von osm2streets. Die Straßenflächen baut Unity weiter
selbst mit den streckenspezifischen Breiten (auf denen auch die Fahrlinie liegt) — zwei verschiedene
Breitenmodelle vereinigt ergäben Treppenkanten. Gehweg und Randstreifen entstehen als Band entlang des
fertigen Asphaltrands (`RoadSurface.cs`), nicht mehr pro OSM-Weg.

## Einmalig einrichten

```
cd Assets/StoryCycling/Tools/osm2streets
npm install
```

## Für eine Strecke ausführen

```
node convert.mjs <route.gpx> <strecke.osm.xml> <ausgabe.o2s.json>
```

Der Story-Cycling-Editor ruft das automatisch mit `node` auf, wenn beim Bauen einer Route
eine `.o2s.json`-Datei fehlt oder älter als die OSM-Datei ist (Menü "Story Cycling/WorldGen/
Build OSM2Streets Geometry for Selected Route", oder automatisch beim Bauen der Welt). Ist
kein `node` installiert oder `npm install` nicht ausgeführt, baut die Pipeline wie bisher mit
unserer eigenen Flächenvereinigung weiter — dieser Schritt ist ein optionales Zusatzwerkzeug,
kein Pflichtschritt.
