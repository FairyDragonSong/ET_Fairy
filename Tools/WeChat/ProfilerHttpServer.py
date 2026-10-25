"""Static experiment resources plus a bounded JSON benchmark receiver on the local LAN."""
import argparse
import datetime
import http.server
import json
from pathlib import Path
import re
import urllib.parse
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('port', type=int)
    parser.add_argument('--bind', required=True)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--results', type=Path, required=True)
    parser.add_argument('--resource-prefix', default='')
    args = parser.parse_args()
    if args.resource_prefix and not re.fullmatch(r'/__redblue__/[A-Za-z0-9_-]+', args.resource_prefix):
        parser.error('Invalid resource prefix')
    args.results.mkdir(parents=True, exist_ok=True)

    class Handler(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *a, **kw):
            super().__init__(*a, directory=str(args.directory.resolve()), **kw)

        def end_headers(self):
            self.send_header('Access-Control-Allow-Origin', '*')
            super().end_headers()

        def translate_path(self, path):
            url = urllib.parse.urlsplit(path)
            if args.resource_prefix and url.path.startswith(args.resource_prefix + '/StreamingAssets/'):
                path = urllib.parse.urlunsplit(('', '', url.path[len(args.resource_prefix):], url.query, ''))
            return super().translate_path(path)

        def do_OPTIONS(self):
            self.send_response(204)
            self.send_header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS')
            self.send_header('Access-Control-Allow-Headers', 'Content-Type')
            self.end_headers()

        def do_POST(self):
            if self.path not in ('/benchmark', '/diagnostic', '/frame-benchmark'):
                self.send_error(404)
                return
            try:
                size = int(self.headers.get('Content-Length', '0'))
                if not 0 < size <= 262144:
                    raise ValueError('Invalid body size')
                result = json.loads(self.rfile.read(size))
                if self.path == '/benchmark':
                    if result.get('schema') != 1 or result.get('count') != 50000 or result.get('measuredTicks') != 300:
                        raise ValueError('Expected fixed-tick RedBlue CPU benchmark')
                elif self.path == '/frame-benchmark':
                    if result.get('schema') != 1 or result.get('kind') != 'frame' or result.get('count') != 50000 or result.get('frames', 0) <= 0:
                        raise ValueError('Expected normal-frame RedBlue benchmark')
                elif result.get('schema') != 1 or result.get('kind') != 'startup':
                    raise ValueError('Expected startup diagnostic')
            except (ValueError, TypeError, AttributeError):
                self.send_error(400)
                return
            now = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
            record = {'receivedUtc': now, 'peer': self.client_address[0], 'result': result}
            folder = args.results if self.path == '/benchmark' else args.results / ('frames' if self.path == '/frame-benchmark' else 'diagnostics')
            folder.mkdir(parents=True, exist_ok=True)
            target = folder / (now + '-' + uuid.uuid4().hex[:8] + '.json')
            target.write_text(json.dumps(record, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"saved":true}')

    http.server.ThreadingHTTPServer((args.bind, args.port), Handler).serve_forever()


if __name__ == '__main__':
    main()
