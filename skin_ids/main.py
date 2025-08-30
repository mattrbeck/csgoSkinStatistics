import atexit
import json
import logging
import re
import subprocess

from apscheduler.schedulers.background import BackgroundScheduler
from flask import Flask

logging.basicConfig(format="%(asctime)s | %(name)s | thread:%(thread)s | %(levelname)s | %(message)s",
                    level=logging.INFO)
LOG = logging.getLogger('SKIN ID GETTER')

app = Flask(__name__, static_url_path='')


@app.route('/')
def main():
    return "SUP FUCKERS"


@app.route('/get_skin_ids')
def get_items():
    return app.send_static_file('skin_ids.json')


def update_csgo_items():
    p = subprocess.run(['steamcmd/steamcmd.sh', '+login', 'anonymous', '+force_install_dir', '../games', '+app_update', '740', '+quit'], stdout=subprocess.DEVNULL)
    if p.returncode == 0:
        update_items_json()
    else:
        LOG.error('Failed to update game')


def update_items_json():
    name_matcher = re.compile(r'^\s*"(Paint[^"]+_Tag)"\s*"([^"]+)"$', re.I)
    names = {}
    with open('games/csgo/resource/csgo_english.txt', 'r', encoding='utf-16-le') as f:
        for line in f.readlines():
            match = name_matcher.match(line)
            if match:
                tag, name = match.groups()
                tag = tag.lower()
                names[tag] = name

    id_matcher = re.compile(r'^.*"(\d+)"$')
    tag_matcher = re.compile(r'^.*"description_tag".*"#(.*)"$')
    ids = {}
    with open('games/csgo/scripts/items/items_game.txt', 'r') as f:
        in_paint_kits = True
        for line in f.readlines():
            if not in_paint_kits and '"paint_kits"' in line:
                in_paint_kits = True
                continue

            id_match = id_matcher.match(line)
            if id_match:
                cur_id = id_match.groups()[0]
                continue

            tag_match = tag_matcher.match(line)
            if tag_match:
                tag = tag_match.groups()[0]
                tag = tag.lower()
                ids[cur_id] = tag

    final_map = {}
    for id_num, tag in ids.items():
        final_map[id_num] = names.get(tag)

    with open('static/skin_ids.json', 'w') as f:
        f.write(json.dumps(final_map))


if __name__ == '__main__':
    update_csgo_items()
    sched = BackgroundScheduler()
    sched.add_job(update_csgo_items, 'interval', minutes=30)
    sched.start()
    atexit.register(lambda: sched.shutdown())

    app.run(debug=True, use_reloader=False, host='0.0.0.0', port=5001)
