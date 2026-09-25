#!/usr/bin/env node
// Wandelt eine Overpass-OSM-Datei (Story-Cycling-Format: Knoten teils direkt an <nd> mit lat/lon,
// teils als eigene <node>) über die Bibliothek osm2streets (https://github.com/a-b-street/osm2streets,
// Apache-2.0) in robuste Straßen- und Kreuzungsflächen um. osm2streets löst genau die Fälle, an denen
// unser eigener Algorithmus zuverlässig scheitert: Doppelfahrbahnen, versetzte "Dog-Leg"-Kreuzungen,
// Kreisverkehre mit Bypass-Spuren. Ausgabe: eine JSON-Datei mit den Umrissen der reinen Fahrbahn
// (ohne Gehweg/Randstreifen — die bleiben unsere eigene, streckenspezifische Logik) in denselben
// lokalen Metern wie der Rest der Pipeline (Ursprung = erster GPX-Punkt, wie GpxParser.cs).
//
// Aufruf:  node convert.mjs <route.gpx> <strecke.osm.xml> <ausgabe.o2s.json> [corridorMeter=170]
//
// Einmalig vor dem ersten Lauf: npm install (in diesem Ordner).
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { initSync, JsStreetNetwork } from "osm2streets-js/osm2streets_js.js";

const R = 6371000.0; // Erdradius; dieselbe Formel wie GpxParser.ProjectToLocalMeters
const here = path.dirname(fileURLToPath(import.meta.url));

function fail(msg) {
  console.error(`osm2streets-Konvertierung: ${msg}`);
  process.exit(1);
}

const [, , gpxPath, osmPath, outPath, corridorArg] = process.argv;
if (!gpxPath || !osmPath || !outPath) {
  fail("Aufruf: node convert.mjs <route.gpx> <strecke.osm.xml> <ausgabe.o2s.json> [corridorMeter]");
}
const corridor = Number(corridorArg || 170);

let gpxText, osmText;
try { gpxText = fs.readFileSync(gpxPath, "utf8"); } catch (e) { fail(`GPX nicht lesbar (${gpxPath}): ${e.message}`); }
try { osmText = fs.readFileSync(osmPath, "utf8"); } catch (e) { fail(`OSM-Datei nicht lesbar (${osmPath}): ${e.message}`); }

// --- Ursprung + Routen-Polylinie (für den Korridor-Filter, wie RoadNet.ScopeDistance) ---
const trk = [...gpxText.matchAll(/<trkpt\s+lat="([-\d.]+)"\s+lon="([-\d.]+)"/g)].map(m => [Number(m[1]), Number(m[2])]);
if (trk.length < 2) fail("GPX enthält keine (oder zu wenige) Trackpunkte.");
const [lat0, lon0] = trk[0];
const lat0rad = (lat0 * Math.PI) / 180;
function toLocal(lat, lon) {
  const x = R * (((lon - lon0) * Math.PI) / 180) * Math.cos(lat0rad); // Ost
  const z = R * (((lat - lat0) * Math.PI) / 180);                     // Nord
  return [x, z];
}
const route = trk.map(([la, lo]) => toLocal(la, lo));
function distToRoute(x, z) {
  let best = Infinity;
  // Grob genügend: Streckenlänge ist überschaubar, kein Bedarf für einen Spatial-Index hier
  for (let i = 1; i < route.length; i++) {
    const [ax, az] = route[i - 1], [bx, bz] = route[i];
    const vx = bx - ax, vz = bz - az;
    const L = vx * vx + vz * vz;
    const t = L === 0 ? 0 : Math.max(0, Math.min(1, ((x - ax) * vx + (z - az) * vz) / L));
    const dx = ax + t * vx - x, dz = az + t * vz - z;
    const d = Math.sqrt(dx * dx + dz * dz);
    if (d < best) best = d;
  }
  return best;
}

// --- Overpass-Dump auf Standard-OSM-XML normalisieren (jeder Knoten als eigenes <node>) ---
const nodes = new Map();
for (const m of osmText.matchAll(/<node id="(\d+)" lat="([-\d.]+)" lon="([-\d.]+)"(\s*\/>|>[\s\S]*?<\/node>)/g))
  nodes.set(m[1], { lat: m[2], lon: m[3] });
const ways = [];
for (const m of osmText.matchAll(/<way id="(\d+)"[^>]*>([\s\S]*?)<\/way>/g)) {
  const body = m[2];
  if (!/k="highway"/.test(body)) continue;
  const nds = [...body.matchAll(/<nd ref="(\d+)"(?:\s+lat="([-\d.]+)"\s+lon="([-\d.]+)")?\s*\/>/g)];
  for (const n of nds) if (n[2] && !nodes.has(n[1])) nodes.set(n[1], { lat: n[2], lon: n[3] });
  const tags = [...body.matchAll(/<tag k="[^"]*" v="[^"]*"\s*\/>/g)].map(t => t[0]).join("");
  ways.push(`<way id="${m[1]}">${nds.map(n => `<nd ref="${n[1]}"/>`).join("")}${tags}</way>`);
}
if (ways.length === 0) fail("Keine befahrbaren Wege (highway=*) in der OSM-Datei gefunden.");
let minlat = 90, minlon = 180, maxlat = -90, maxlon = -180;
for (const n of nodes.values()) {
  minlat = Math.min(minlat, +n.lat); maxlat = Math.max(maxlat, +n.lat);
  minlon = Math.min(minlon, +n.lon); maxlon = Math.max(maxlon, +n.lon);
}
const stdXml =
  `<?xml version="1.0" encoding="UTF-8"?><osm version="0.6">` +
  `<bounds minlat="${minlat}" minlon="${minlon}" maxlat="${maxlat}" maxlon="${maxlon}"/>` +
  [...nodes].map(([id, n]) => `<node id="${id}" lat="${n.lat}" lon="${n.lon}"/>`).join("") +
  ways.join("") + `</osm>`;

// --- osm2streets ---
initSync(fs.readFileSync(path.join(here, "node_modules/osm2streets-js/osm2streets_js_bg.wasm")));
let net;
try {
  net = new JsStreetNetwork(stdXml, "", {
    debug_each_step: false,
    // Wir wollen NUR die reine Fahrbahn (Asphalt) — Gehweg/Randstreifen bleiben unsere eigene,
    // pro Strecke konfigurierbare Logik (siehe RoadSurface.cs OuterW). Deshalb keine inferierten
    // Gehwege: die würden die Fahrbahnfläche selbst aufweiten.
    inferred_sidewalks: false,
    dual_carriageway_experiment: false,
    sidepath_zipping_experiment: false,
    osm2lanes: false,
  });
} catch (e) {
  fail(`osm2streets konnte das Netz nicht aufbauen: ${e.message || e}`);
}
const plain = JSON.parse(net.toGeojsonPlain());

// --- road+intersection Polygone einsammeln, in lokale Meter projizieren, auf den Korridor zuschneiden ---
function ring(geom) {
  // Polygon: [ [ [lon,lat], ... ] ]  (erster Ring = äußere Kontur; osm2streets liefert konvexe/einfache Flächen ohne Löcher)
  const coords = geom.type === "Polygon" ? geom.coordinates[0]
               : geom.type === "MultiPolygon" ? geom.coordinates[0][0]
               : null;
  return coords ? coords.map(([lon, lat]) => toLocal(lat, lon)) : null;
}
const polys = [];
let kept = 0, dropped = 0, degenerate = 0;
for (const f of plain.features) {
  const t = f.properties.type;
  if (t !== "road" && t !== "intersection") continue;
  const pts = ring(f.geometry);
  if (!pts || pts.length < 3) { degenerate++; continue; }
  const cx = pts.reduce((s, p) => s + p[0], 0) / pts.length;
  const cz = pts.reduce((s, p) => s + p[1], 0) / pts.length;
  if (distToRoute(cx, cz) > corridor) { dropped++; continue; }
  polys.push(pts.map(([x, z]) => [Math.round(x * 100) / 100, Math.round(z * 100) / 100]));
  kept++;
}
if (kept === 0) fail("Nach dem Korridor-Zuschnitt sind keine Straßenflächen übrig — GPX/OSM passen nicht zusammen?");

fs.writeFileSync(outPath, JSON.stringify({ origin: { lat: lat0, lon: lon0 }, corridor, polygons: polys }));
console.log(`osm2streets: ${plain.features.length} Objekte (${kept} im Korridor, ${dropped} außerhalb, ${degenerate} entartet) -> ${outPath}`);
