# osm2streets-Konvertierung

Erzeugt aus der OSM-Datei einer Strecke robuste Fahrbahn- und Kreuzungsflächen mit
[osm2streets](https://github.com/a-b-street/osm2streets) (Apache-2.0), statt sie mit
unserem eigenen Flächen-Code selbst zu konstruieren. osm2streets ist ein von der
OpenStreetMap-Community entwickeltes, gepflegtes Projekt, das genau die Fälle löst, an
denen ein selbstgebauter Algorithmus zuverlässig scheitert: Doppelfahrbahnen, versetzte
"Dog-Leg"-Kreuzungen, Kreisverkehre mit Bypass-Spuren.

Gehweg, Randstreifen und alle streckenspezifischen Regeln (z. B. der breite Seitenstreifen
auf der Victoria Road) bleiben unverändert unsere eigene Logik (`RoadSurface.cs`) — nur die
reine Fahrbahnfläche kommt von osm2streets.

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
