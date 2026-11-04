"""Record the current client compilation inputs; never assert runtime QA approval."""
from pathlib import Path
import hashlib
import json

root = Path(__file__).resolve().parents[1] / 'next' / 'client-2.0'
path = root / 'SOURCE-MANIFEST.json'
manifest = json.loads(path.read_text(encoding='utf-8-sig'))
paths = {item['path'] for item in manifest['directCsSources']}
paths.add('ClientEngine/InstallSessionRules.cs')
manifest['directCsSources'] = [
    {'path': name, 'sha256': hashlib.sha256((root / name).read_bytes()).hexdigest().upper()}
    for name in sorted(paths)
]
manifest.update(buildRevision=38, fileVersion='2.0.145.0', informationalVersion='2.0.5+patch.r3',
    scope='Current patch source inputs only. Installer identity and dedicated Windows 10/11 QA are recorded separately; this manifest is not release approval.')
path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f"Recorded {len(paths)} direct C# sources")
