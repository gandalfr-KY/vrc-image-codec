"""Package the same encoder assets for VPM and Assets-based installation."""
from pathlib import Path
import hashlib
import io
import json
import re
import tarfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
metadata = json.loads((ROOT / 'package.json').read_text(encoding='utf-8'))
release = ROOT / 'release'
release.mkdir(exist_ok=True)
name = metadata['name'] + '-' + metadata['version']
files = sorted(path for path in ROOT.rglob('*') if path.is_file() and not any(
    part.startswith('.') or part in ['release', 'test-results', 'Tests~', 'Tools~', '__pycache__']
    for part in path.relative_to(ROOT).parts
))
with zipfile.ZipFile(release / (name + '.zip'), 'w', zipfile.ZIP_DEFLATED) as package:
    for path in files:
        package.write(path, path.relative_to(ROOT).as_posix())

def add(archive, name, content):
    entry = tarfile.TarInfo(name)
    entry.size = len(content)
    entry.mtime = 0
    archive.addfile(entry, io.BytesIO(content))

with tarfile.open(release / (name + '.unitypackage'), 'w:gz') as package:
    for path in files:
        relative = path.relative_to(ROOT).as_posix()
        if not relative.endswith('.meta') or relative.startswith('Samples~/'):
            continue
        guid = re.search(r'^guid: ([a-f0-9]{32})$', path.read_text(encoding='utf-8'), re.M)[1]
        asset = Path(str(path)[:-5])
        add(package, guid + '/asset.meta', path.read_bytes())
        add(package, guid + '/pathname', ('Assets/VrcAvifEncoder/' + relative[:-5]).encode())
        if asset.is_file():
            add(package, guid + '/asset', asset.read_bytes())
for path in sorted(release.iterdir()):
    if path.suffix not in ['.zip', '.unitypackage']:
        continue
    path.with_name(path.name + '.sha256').write_text(hashlib.sha256(path.read_bytes()).hexdigest() + '  ' + path.name + '\n', encoding='utf-8')
    print(path.name)
