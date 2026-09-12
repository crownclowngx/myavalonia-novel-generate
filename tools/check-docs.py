"""Validate local Markdown targets (including explicit/GitHub-style anchors)."""
from pathlib import Path
from urllib.parse import unquote
import re
root = Path(__file__).resolve().parents[1]
errors = []
count = 0
for file in [root / 'README.md', *sorted((root / 'docs').rglob('*.md'))]:
    body = re.sub(r'^```.*?^```', '', file.read_text(encoding='utf-8'), flags=re.M | re.S)
    for match in re.finditer(r'\[[^\]\n]*\]\(([^)\n]+)\)', body):
        target = match[1].strip('<>')
        if re.match(r'[a-zA-Z]+:', target):
            continue
        name, _, anchor = unquote(target).partition('#')
        dest = (file.parent / name).resolve() if name else file
        count += 1
        if not dest.exists():
            errors.append(f'{file.relative_to(root)}: missing {target}')
        elif anchor and dest.suffix == '.md':
            content = dest.read_text(encoding='utf-8')
            anchors = set(re.findall(r'id="([^"]+)"', content))
            for heading in re.findall(r'^#{1,6}\s+(.+)$', content, re.M):
                anchors.add(re.sub(r'[^\w\- ]', '', heading.lower()).replace(' ', '-'))
            if anchor not in anchors:
                errors.append(f'{file.relative_to(root)}: missing anchor {target}')
if errors:
    raise SystemExit('\n'.join(errors))
print(f'Markdown local links passed: {count}')
