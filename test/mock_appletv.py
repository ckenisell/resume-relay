"""A stand-in Apple TV for the tests, over plain HTTP on 127.0.0.10:8080 (Resume Relay talks to it instead of the real
protocol when RESUMERELAY_APPLETV_MOCK is set; the real protocol is AppleTvControlLibrary's, which has its own tests
against a simulated Apple TV). It shows PIN 1234 when pairing starts.

    /_test/play?appId=&appName=&title=&series=&season=&episode=&year=&pos=&len=&paused=1   what an app is playing
    /_test/stop                                  nothing playing
    /_test/requests                              the requests seen (newest last)
"""
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

IP, PORT = "127.0.0.10", 8080
PIN, CRED = "1234", "mock-cred-7f3a"
STATE = dict(pairing=False, paired=set(), now=None, requests=[])
PNG = {"data": b""}
LOCK = threading.Lock()


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

    def parts(self):
        u = urlparse(self.path)
        with LOCK:
            STATE["requests"].append(u.path)
            del STATE["requests"][:-300]
        return u.path, {k: v[0] for k, v in parse_qs(u.query).items()}

    def do_POST(self):
        path, q = self.parts()
        n = int(self.headers.get("Content-Length") or 0)
        if n:
            self.rfile.read(n)
        with LOCK:
            if path == "/pair/begin":
                STATE["pairing"] = True
                return self.send({"ok": True})
            if path == "/pair/finish":
                if not STATE["pairing"] or q.get("pin") != PIN:
                    return self.send({"error": "wrong PIN"}, 470)
                STATE["pairing"] = False
                STATE["paired"].add(CRED)
                return self.send({"credentials": CRED})
        return self.send({"error": "unknown"}, 404)

    def do_GET(self):
        path, q = self.parts()
        if path == "/info":
            return self.send(dict(name="Family Room", model="AppleTV11,1", uniqueId="AA:BB:CC:DD:EE:FF", port=7000))
        if path == "/state":
            with LOCK:
                if q.get("cred") not in STATE["paired"]:
                    return self.send({"error": "not paired"}, 470)
                return self.send(STATE["now"] or {})
        if path == "/artwork":
            return self.send(None, ctype="image/png", raw=PNG["data"])
        with LOCK:
            if path == "/_test/play":
                STATE["now"] = dict(appId=q.get("appId", ""), appName=q.get("appName", ""), title=q.get("title", ""), series=q.get("series", ""),
                                    season=int(q.get("season", 0)), episode=int(q.get("episode", 0)), year=int(q.get("year", 0)),
                                    pos=int(q.get("pos", 0)), len=int(q.get("len", 0)), paused=q.get("paused") == "1")
                return self.send({"ok": True})
            if path == "/_test/stop":
                STATE["now"] = None
                return self.send({"ok": True})
            if path == "/_test/requests":
                return self.send(STATE["requests"][-200:])
        return self.send({"error": "unknown"}, 404)


def start(png):
    PNG["data"] = png
    threading.Thread(target=ThreadingHTTPServer((IP, PORT), H).serve_forever, daemon=True).start()
