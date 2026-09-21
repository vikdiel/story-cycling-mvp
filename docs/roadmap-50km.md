# Roadmap — 50 km Rennrad-Welt (asset-getrieben)

Entscheidungen (2026-09-21):
- **Projekt:** bestehendes `storycycling-unity` erweitern (neues Modul `StoryCycling.WorldGen`,
  altes CapeCrown-System bleibt unangetastet).
- **Pipeline:** URP.
- **Terrain:** GPX-Höhenprofil auf generischem Terrain-Korridor (kein Cesium).
- **Assets:** vorhandene Synty-Packs (PolygonCity, PolygonGeneric).

Hartes Prinzip: Generator ist **Platzierer, nicht Erfinder**. Nur Prefabs aus dem
Asset-Katalog instanziieren. Fehlt ein Asset → Stelle leer lassen + Warnung loggen,
niemals Platzhalter-Geometrie erfinden. Deterministisch über globalen Seed.

## Module (Zielarchitektur)
GPX → GpxParser → Punkte → RouteComposer → ein Spline → WorldGenerator (nur Katalog)
→ RiderController. Trainer über externen Service + WebSocket (kein BLE in Unity).

## Milestones (Baureihenfolge)
1. Asset-Katalog-System: `AssetEntry`, `Biome`, `AssetCatalog` (ScriptableObjects)
   + Editor-Auto-Scan der Synty-Prefabs.
2. `GpxParser` (lat/lon/ele → lokale Meter) → Spline rendern.
3. `RouteComposer`: GPX + synthetische Segmente nahtlos zu einem Spline.
4. `WorldGenerator` v1: Filler-Platzierung entlang Spline, nur Katalog, chunked, deterministisch.
5. Key-Elemente aus `RouteManifest` (mit Sperrzone).
6. `TrainerBridge` (WebSocket) + Dummy-Service → echter FTMS-Service.
7. `RiderController`: Bewegung am Spline, Steigung→Sim, HUD.
8. Politur: LOD, GPU-Instancing, Chunk-Streaming.

## Akzeptanzkriterien v1
- GPX + synthetisches Segment = durchgehende befahrbare Strecke.
- Sichtbare Welt nur aus Katalog-Prefabs; jede Instanz mit Quell-Asset geloggt.
- Gleicher Seed → identische Welt.
- 5–10 Key-Elemente an definierten Positionen.
- Trainer-Steigung steuert Widerstand; Avatar-Speed folgt Leistung.
- 50 km ohne Framerate-Einbruch (Chunk-Streaming).

## Status
- [x] Milestone 1: Katalog-System (AssetEntry/Biome/AssetCatalog + Scan-Tool)
- [ ] Milestone 2: GpxParser
- [ ] Milestone 3: RouteComposer
- [ ] Milestone 4: WorldGenerator v1
- [ ] Milestone 5: Key-Elemente
- [ ] Milestone 6: TrainerBridge
- [ ] Milestone 7: RiderController
- [ ] Milestone 8: Politur
