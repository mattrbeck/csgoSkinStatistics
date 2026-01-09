from gevent import monkey
monkey.patch_all()
from gevent.pywsgi import WSGIServer
from csgo_worker import CSGOWorker
from flask import Flask, request, current_app

import re

import os
import sys

import logging

logging.basicConfig(format="%(asctime)s | %(name)s | thread:%(thread)s | %(levelname)s | %(message)s",
                    level=logging.INFO)
LOG = logging.getLogger('CSGO GC API')

app = Flask('Flask Server')


@app.route('/')
def home():
    return current_app.send_static_file('index.html')


@app.route('/api', methods=['GET'])
def item() -> str:
    if 'url' in request.args:
        match = re.search('([SM])(\d+)A(\d+)D(\d+)$', request.args['url'])
        if 'S' == match.group(1):
            s = int(match.group(2))
            m = 0
        else:
            s = 0
            m = int(match.group(2))
        a = int(match.group(3))
        d = int(match.group(4))
    else:
        s = int(request.args['s'])
        a = int(request.args['a'])
        d = int(request.args['d'])
        m = int(request.args['m'])

    try:
        iteminfo = worker.get_item(s, a, d, m)
    except TypeError:
        LOG.info('Failed response')
        return 'Invalid link or Steam is slow.'

    return str(iteminfo)


@app.route('/ping', methods=["POST"])
def ping() -> str:
    return 'pong'


if __name__ == "__main__":
    LOG.info("csgoSkinStatistics")
    LOG.info("-" * 18)
    LOG.info("Starting worker...")

    worker = CSGOWorker()

    try:
        if os.environ.get('steam_user') and os.environ.get('steam_pass'):
            try:
                worker.start(username=os.environ['steam_user'],
                             password=os.environ['steam_pass'])
            except:
                LOG.error('Failed to sign in with environment variables')
                raise
        elif len(sys.argv) == 3:
            try:
                worker.start(username=sys.argv[1], password=sys.argv[2])
            except:
                LOG.error('Failed to sign in with args')
                raise
        else:
            try:
                worker.cli_login()
            except:
                LOG.error('Failed to with in through the CLI')
                raise
    except:
        LOG.info('Exiting...')
        worker.close()
        sys.exit()

    LOG.info("Starting HTTP server...")
    http_server = WSGIServer(('', 5000), app, log=None)

    try:
        http_server.serve_forever()
    except KeyboardInterrupt:
        LOG.info("Exit requested")
        worker.close()
