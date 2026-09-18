"""Dependency preflight only. Does NOT claim to compile or render Unity."""
from pathlib import Path
import re
root = Path('Assets/StoryCycling')
builders = '\n'.join(p.read_text() for p in (root/'Editor').glob('*.cs'))
paths = set(re.findall(r'Root \+ "([^"]+\.prefab)"', builders))
assert paths, 'No art dependencies found'
for path in paths:
    assert (Path('Assets/Synty/PolygonCity/Prefabs')/path).is_file(), path
for path in set(re.findall(r'"(Assets/[^"]+\.prefab)"', builders)):
    assert Path(path).is_file(), path
for p in root.rglob('*.cs'):
    assert Path(str(p)+'.meta').is_file(), f'Missing meta: {p}'
    assert not re.search('TooMoose|Palmov|FREE_CartoonPack',p.read_text()), p
print(f'Source/dependency preflight PASS ({len(paths)} prefabs). Unity compilation, geometry execution and visual review are separate gates.')
