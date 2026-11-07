"""A stand-in TMDB (api.themoviedb.org/3 and image.tmdb.org/t/p/original) for the tests, on 127.0.0.11:8080. The key
must be "test-key". Wednesday has posters of every kind, so the choice can be checked: the best is the English
2000x3000 one with the most votes (best-show.png), not the bigger-voted Spanish one, the smaller English ones, the
textless one or the one that isn't 2:3.

    /_test/fetched     the picture files fetched (newest last)
"""
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

IP, PORT, KEY = "127.0.0.11", 8080, "test-key"
FETCHED = []
PNG = {"data": b""}

TV = [dict(id=119051, name="Wednesday", first_air_date="2022-11-23", original_language="en", media_type="tv"),
      dict(id=555001, name="Wednesday Addams Specials", first_air_date="2020-01-01", original_language="en", media_type="tv")]
MOVIES = [dict(id=693134, title="Dune: Part Two", release_date="2024-02-27", original_language="en", media_type="movie")]


def poster(f, w, h, lang, votes, count=10):
    return dict(file_path=f, width=w, height=h, aspect_ratio=w / h, iso_639_1=lang, vote_average=votes, vote_count=count)


IMAGES = {
    "/tv/119051": [poster("/es-big.png", 2000, 3000, "es", 9.5), poster("/en-small.png", 1000, 1500, "en", 9.0),
                   poster("/en-mid.png", 1920, 2880, "en", 8.0), poster("/best-show.png", 2000, 3000, "en", 6.5),
                   poster("/en-big-low.png", 2000, 3000, "en", 5.0), poster("/textless.png", 2000, 3000, None, 7.0),
                   poster("/not-23.png", 2000, 2800, "en", 10.0)],
    "/tv/119051/season/1": [poster("/season1-small.png", 1000, 1500, "en", 9.0), poster("/season1.png", 2000, 3000, "en", 5.5)],
    "/movie/693134": [poster("/dune.png", 2000, 3000, "en", 6.0), poster("/dune-1500.png", 1000, 1500, "en", 9.0)],
}


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, obj, code=200, ctype="application/json", raw=None):
        body = raw if raw is not None else json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        p = u.path
        if p.startswith("/t/p/original/"):
            FETCHED.append(p[len("/t/p/original"):])
            return self.send(None, ctype="image/png", raw=PNG["data"])
        if p == "/_test/fetched":
            return self.send(FETCHED[-100:])
        if not p.startswith("/3/"):
            return self.send({"status_message": "unknown"}, 404)
        if q.get("api_key") != KEY:
            return self.send({"status_message": "Invalid API key"}, 401)
        p = p[2:]
        query = q.get("query", "").lower()
        if p == "/search/tv":
            return self.send(dict(results=[r for r in TV if query in r["name"].lower()]))
        if p == "/search/movie":
            return self.send(dict(results=[r for r in MOVIES if query in r["title"].lower()]))
        if p == "/search/multi":
            return self.send(dict(results=[r for r in TV if query in r["name"].lower()] + [r for r in MOVIES if query in r["title"].lower()]))
        if p.endswith("/images"):
            return self.send(dict(posters=IMAGES.get(p[:-len("/images")], [])))
        return self.send({"status_message": "unknown"}, 404)


def start(png):
    PNG["data"] = png
    threading.Thread(target=ThreadingHTTPServer((IP, PORT), H).serve_forever, daemon=True).start()
