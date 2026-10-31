"""Builds tools/data/graphics-options.json from DiRT 3's system/hardware_settings_options.xml.

Read-only: the game file is parsed, never changed. DiRT3VR always runs DirectX 11, so
preset values use the game's dx_11_option where one exists; the DirectX 9 differences are
kept separately. Run with --check to compare against the committed catalog.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ElementTree

DEFAULT_GAME = Path(r'C:\Program Files (x86)\Steam\steamapps\common\DiRT 3 Complete Edition')
CATALOG_PATH = Path(__file__).with_name('data') / 'graphics-options.json'

# Quality categories only: display, stereo, audio, CPU and physics (damage) are not graphics quality.
QUALITY = [
    ('NightLighting', 'night_lighting', 'Night lighting'),
    ('Shadows', 'shadows', 'Shadows'),
    ('Particles', 'particles', 'Particles'),
    ('Mirrors', 'mirrors', 'Mirrors'),
    ('Crowd', 'crowd', 'Crowd'),
    ('Cloth', 'cloth', 'Cloth'),
    ('AmbientOcclusion', 'dynamic_ambient_occ', 'Ambient occlusion'),
    ('GroundCover', 'groundCover', 'Ground cover'),
    ('Vehicles', 'vehicles', 'Vehicle detail'),
    ('Trees', 'trees', 'Trees'),
    ('Objects', 'objects', 'Objects'),
    ('CarReflections', 'car_reflection', 'Car reflections'),
    ('Water', 'water', 'Water'),
    ('PostProcessing', 'post', 'Post-processing'),
    ('Skidmarks', 'skidmarks', 'Skidmarks'),
]
LABELS = {'ultralow': 'Ultra Low', 'low': 'Low', 'medium': 'Medium', 'high': 'High', 'ultra': 'Ultra',
          'off': 'Off', 'on': 'On'}
PRESETS = ['ultralow', 'low', 'medium', 'high', 'ultra']
# Settings the presets also set but that are not part of the quality catalog.
OTHER_PRESET_CATEGORIES = ['multisampling', 'physics']


def _attributes(element):
    """Flatten an option's setting elements into element@attribute (graphics_card/... kept as a path)."""
    result = {}

    def walk(node, prefix):
        for child in node:
            if child.tag == 'ref':
                continue
            path = prefix + child.tag
            for name, value in child.attrib.items():
                result[path + '@' + name] = value
            walk(child, path + '/')

    walk(element, '')
    return result


def build(options_path):
    data = Path(options_path).read_bytes()
    root = ElementTree.fromstring(data)
    categories = {category.get('name'): category for category in root.findall('category')}
    detail = categories['graphicsDetail']
    presets = {option.get('name'): option for option in detail.findall('option')}
    default_preset = next(name for name, option in presets.items() if option.get('type') == 'default')

    def resolved(preset, category, dx11=True):
        ref = next(ref for ref in presets[preset].findall('ref') if ref.get('category') == category)
        return ref.get('dx_11_option') if dx11 and ref.get('dx_11_option') else ref.get('option')

    out_categories = []
    for field, name, label in QUALITY:
        choices = []
        for option in categories[name].findall('option'):
            choice = {'id': option.get('name'), 'label': LABELS[option.get('name')],
                      'attributes': _attributes(option)}
            if option.get('dx11Only') == 'true':
                choice['dx11Only'] = True
            choices.append(choice)
        out_categories.append({'field': field, 'gameCategory': name, 'label': label,
                               'default': resolved(default_preset, name), 'choices': choices})

    out_presets = []
    for preset in PRESETS:
        values = {field: resolved(preset, name) for field, name, _ in QUALITY}
        dx9 = {field: resolved(preset, name, dx11=False) for field, name, _ in QUALITY
               if resolved(preset, name, dx11=False) != values[field]}
        entry = {'id': preset, 'label': LABELS[preset], 'values': values,
                 'other': {name: resolved(preset, name) for name in OTHER_PRESET_CATEGORIES},
                 'graphicsDetailLevel': presets[preset].find('graphics_detail').get('level')}
        if dx9:
            entry['dx9Differences'] = dx9
        if preset == default_preset:
            entry['gameDefault'] = True
        out_presets.append(entry)

    return {
        'schema': 1,
        'game': 'DiRT 3 Complete Edition (Steam)',
        'sourceOptionsSha256': hashlib.sha256(data).hexdigest(),
        'policy': ('Quality settings only, written to Documents\\My Games\\DiRT3\\hardwaresettings\\'
                   'hardware_settings_config.xml as element@attribute. Values are DirectX 11 (DiRT3VR '
                   'forces forcedx9=false). Display, multisampling, stereo, audio, CPU and physics '
                   '(damage) are not quality settings and are listed per preset under "other" only. '
                   'A choice sets only the attributes the game itself sets; others stay unchanged. '
                   'Defaults are the game default preset (' + default_preset + ').'),
        'categories': out_categories,
        'presets': out_presets,
    }


def render(catalog):
    return json.dumps(catalog, indent=2, ensure_ascii=False) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, default=DEFAULT_GAME, help='DiRT 3 Complete Edition folder')
    parser.add_argument('--check', action='store_true', help='fail if the committed catalog differs')
    args = parser.parse_args()
    text = render(build(args.game / 'system' / 'hardware_settings_options.xml'))
    if args.check:
        if CATALOG_PATH.read_text(encoding='utf-8') != text:
            print('graphics-options.json differs from the game file', file=sys.stderr)
            return 1
        print('graphics-options.json matches the game file')
        return 0
    CATALOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    CATALOG_PATH.write_text(text, encoding='utf-8', newline='\n')
    print('wrote', CATALOG_PATH)
    return 0


if __name__ == '__main__':
    sys.exit(main())
