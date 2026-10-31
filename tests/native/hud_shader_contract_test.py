"""Verify the production UI shader whitelist against the installed asset pack."""
import importlib.util
import json
from pathlib import Path
import re
import sys

root, pack = Path(sys.argv[1]), Path(sys.argv[2])
if not pack.is_file():
    print('Installed UI shader pack unavailable; contract test skipped')
    sys.exit(77)
spec = importlib.util.spec_from_file_location('inspect_ui', root / 'tools/research/inspect_ui_shaders.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
actual = module.inspect(pack)
recorded = json.loads((root / 'docs/research/ui-shaders.json').read_text(encoding='utf-8'))
assert actual['sha256'] == recorded['sha256']
source = (root / 'src/hud_shaders.h').read_text(encoding='utf-8')
for key, kind in [('exclusiveTwoDimensionalVertexHashes', 'TwoDimensional'),
                  ('sharedWithOtherUiHashes', 'Shared')]:
    group = re.search(r'((?:    case 0x[0-9a-f]+ull:\n)+)        return HudShaderKind::' + kind, source).group(1)
    hashes = re.findall(r'0x([0-9a-f]+)ull', group)
    assert hashes == actual[key] == recorded[key], kind + ' classifier differs from shipped asset'
print(f"Verified {len(actual['exclusiveTwoDimensionalVertexHashes'])} dedicated 2D and "
      f"{len(actual['sharedWithOtherUiHashes'])} shared UI shader hashes against the fingerprinted DiRT3 asset")
