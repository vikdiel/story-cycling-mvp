"""Dependency preflight only. Does NOT claim to compile or render Unity."""
from pathlib import Path
import re
root = Path('Assets/StoryCycling')
builder = (root/'Editor/CapeCrownSceneBuilder.cs').read_text()
paths = re.findall(r'Root \+ "([^"]+\.prefab)"', builder)
assert paths, 'No art dependencies found'
for path in paths:
    assert (Path('Assets/Synty/PolygonCity/Prefabs')/path).is_file(), path
for p in root.rglob('*.cs'):
    assert Path(str(p)+'.meta').is_file(), f'Missing meta: {p}'
    assert not re.search('TooMoose|Palmov|FREE_CartoonPack',p.read_text()), p
print(f'Source/dependency preflight PASS ({len(paths)} prefabs). Unity compilation, geometry execution and visual review are separate gates.')
