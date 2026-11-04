"""A stand-in Kaleidescape player for the tests: its control protocol on 127.0.0.9:10000 (one line per message, as in
Kaleidescape's control protocol reference and pykaleidescape), covers and test controls over HTTP on 127.0.0.9:8080.

    /_test/play?handle=H&pos=SECONDS     plays a movie (paused=1 to pause)
    /_test/stop                          back to the movie covers
    /_test/standby?on=0|1                standby / on
    /_test/requests                      the protocol requests seen (newest last)
"""
import socketserver
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs
import json

IP, PORT, HTTP = "127.0.0.9", 10000, 8080
MOVIES = {
    "26-0.0-S_c446c8e2": dict(Title="Dune: Part Two", Year="2024", Length=9960),
    "26-0.0-S_c446c8e3": dict(Title="Top Gun: Maverick", Year="2022", Length=7860),
}
STATE = dict(on=True, handle="", pos=0, paused=False, requests=[])
PNG = {"data": b""}
LOCK = threading.Lock()


def esc(v):
    return str(v).replace("\\", "\\\\").replace(":", "\\:").replace("/", "\\/")


def line(seq, name, *fields, status="000"):
    body = f"01/{seq}/{status}:{name}:" + "".join(esc(f) + ":" for f in fields)
    return body + "/" + str(sum(body.encode()) % 100).zfill(2) + "\r\n"


def answer(req):
    parts = req.split("/", 2)
    if len(parts) < 3:
        return ""
    seq, rest = parts[1], parts[2]
    name, _, args = rest.partition(":")
    args = [a for a in args.split(":")][:-1] if args else []
    with LOCK:
        STATE["requests"].append(name)
        s = dict(STATE)
    m = MOVIES.get(s["handle"])
    playing = s["on"] and m is not None
    if name == "GET_DEVICE_INFO":
        return line(seq, "DEVICE_INFO", "", "00000000A1B2C3", "01", "127.000.000.009")
    if name == "GET_FRIENDLY_NAME":
        return line(seq, "FRIENDLY_NAME", "Screening Room")
    if name == "GET_DEVICE_TYPE_NAME":
        return line(seq, "DEVICE_TYPE_NAME", "Strato V")
    if name == "GET_DEVICE_POWER_STATE":
        return line(seq, "DEVICE_POWER_STATE", "1" if s["on"] else "0", "1")
    if name == "GET_PLAY_STATUS":
        if not playing:
            return line(seq, "PLAY_STATUS", "0", "0", "00", "00000", "00000", "000", "00000", "00000")
        return line(seq, "PLAY_STATUS", "1" if s["paused"] else "2", "0", "01", f"{m['Length']:05d}", f"{s['pos']:05d}", "001", "00600", "00010")
    if name == "GET_PLAYING_TITLE_NAME":
        return line(seq, "TITLE_NAME", m["Title"] if playing else "")
    if name == "GET_HIGHLIGHTED_SELECTION":
        return line(seq, "HIGHLIGHTED_SELECTION", s["handle"] if playing else "")
    if name == "GET_CONTENT_DETAILS":
        h = args[0] if args else ""
        mv = MOVIES.get(h)
        if not mv:
            return line(seq, "", status="017")
        rows = [("Title", mv["Title"]), ("Year", mv["Year"]), ("Cover_URL", f"http://{IP}:{HTTP}/covers/{h}-small.png"),
                ("HiRes_cover_URL", f"http://{IP}:{HTTP}/covers/{h}.png"), ("Rating", "PG-13")]
        out = line(seq, "CONTENT_DETAILS_OVERVIEW", str(len(rows)), h, "movies")
        for i, (k, v) in enumerate(rows, 1):
            out += line(seq, "CONTENT_DETAILS", str(i), k, v)
        return out
    return line(seq, "", status="010")


class Proto(socketserver.StreamRequestHandler):
    def handle(self):
        while True:
            raw = self.rfile.readline()
            if not raw:
                return
            req = raw.decode("latin-1").strip()
            if req:
                self.wfile.write(answer(req).encode("latin-1"))
                self.wfile.flush()


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
        if u.path.startswith("/covers/"):
            return self.send(None, ctype="image/png", raw=PNG["data"])
        with LOCK:
            if u.path == "/_test/play":
                STATE.update(handle=q.get("handle", ""), pos=int(q.get("pos", 0)), paused=q.get("paused") == "1", on=True)
            elif u.path == "/_test/stop":
                STATE.update(handle="", pos=0, paused=False)
            elif u.path == "/_test/standby":
                STATE.update(on=q.get("on") == "1", handle="" if q.get("on") != "1" else STATE["handle"])
            elif u.path == "/_test/requests":
                return self.send(STATE["requests"][-200:])
            else:
                return self.send({"error": "unknown"}, 404)
        return self.send({"ok": True})


class TS(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def start(png):
    PNG["data"] = png
    threading.Thread(target=TS((IP, PORT), Proto).serve_forever, daemon=True).start()
    threading.Thread(target=ThreadingHTTPServer((IP, HTTP), H).serve_forever, daemon=True).start()
