"""Check source-only layout, local Markdown targets and private-file exclusions."""
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
tracked = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT).decode().split('\0')
errors = []
for name in filter(None, tracked):
    if re.search(r'(^|/)(\.local|\.wix|release-private|내부 배포)/|배포암호|\.(exe|zip|pfx|p12|key|dpapi)$', name, re.I):
        errors.append(f'Private/generated artifact tracked: {name}')
for name in ('.local/release-private/password.txt', '.wix/cache', '관리자ZIP-배포암호.txt', '내부 배포/private.zip', 'private.pfx'):
    result = subprocess.run(['git', 'check-ignore', '--no-index', '-q', '--', name], cwd=ROOT)
    if result.returncode != 0:
        errors.append(f'Private output is not ignored: {name}')
for path in ROOT.rglob('*.md'):
    if '.git' in path.parts or '.local' in path.relative_to(ROOT).parts:
        continue
    for target in re.findall(r'\[[^\]]*\]\(([^)]+)\)', path.read_text(encoding='utf-8-sig')):
        if re.match(r'^[a-zA-Z]+:', target) or target.startswith('#'):
            continue
        target = target.split('#')[0]
        if target and not (path.parent / target).exists():
            errors.append(f'Broken relative link: {path.relative_to(ROOT)} -> {target}')
for version in ('V1.7.7', 'V1.7.8', 'V1.7.9', 'V1.7.10', 'V1.7.11'):
    base = ROOT / 'src' / 'legacy' / f'SMB Manager {version}'
    if not (base / 'source' / 'build.ps1').is_file():
        errors.append(f'Legacy build source missing: {version}')
for name in ('LICENSE-NotoSansCJK.txt', 'NotoSansCJKkr-Regular.otf', 'NotoSansCJKkr-Bold.otf'):
    if not (ROOT / 'assets/fonts/noto-sans-cjk-kr' / name).is_file():
        errors.append(f'Shared font asset missing: {name}')
if errors:
    raise SystemExit('\n'.join(errors))
print('Source layout, Markdown targets and private exclusions passed.')
