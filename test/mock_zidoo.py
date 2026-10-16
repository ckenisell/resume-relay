"""Simulated Zidoo players for testing Resume Relay.

Each player listens on its own loopback IP, port 9529, and answers the same endpoints, in the
same shapes, as the real responses captured with Z-Keeper's API Probe.

Test-only control endpoints (not on real players):
  /_test/play?path=<play path>&pos=<ms>&dur=<ms>   start/advance playback
  /_test/stop                                      stop (player saves its own bookmark, like HT)
  /_test/offline?on=1|0                            simulate asleep/off
  /_test/state?path=<source url>                   read bookmark directly
  /_test/setmac?mac=..                             pretend a different player took this IP
"""
import json, sys, threading, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs

SHARE = "smb://10.0.0.56/Entertainment"
FILES = [
    ("/Movies/Braveheart/Braveheart (1995).4K.(1#Theatrical Movie).mkv", 10664000, "Braveheart"),
    ("/Movies/Braveheart/Featurettes/Braveheart (1995).(7#A Writer's Journal).mkv", 1290000, "Braveheart"),
    ("/TV/Andor/Season 01/Andor - S01E03.mkv", 2400000, "Andor"),
    ("/TV/Andor/Season 01/Andor - S01E04.mkv", 2900000, "Andor"),
    ("/Movies/Arrival (2016)/Arrival (2016).mkv", 6960000, "Arrival"),
]


def now_ms():
    return int(time.time() * 1000)


class Player:
    def __init__(self, name, ip, mac, id_base, buggy_write=False, has_files=None):
        self.name, self.ip, self.mac = name, ip, mac
        self.buggy_write = buggy_write
        self.offline = False
        self.ht_closed = False
        self.playing = None  # dict(path, pos, dur, title)
        self.lock = threading.Lock()
        self.entries = {}   # entry id -> record
        self.by_url = {}
        for i, (rel, dur, title) in enumerate(FILES):
            if has_files is not None and i not in has_files:
                continue
            eid = id_base + i * 7          # different numbering on every player
            title_id = 900 + eid           # the title that holds the file
            rec = dict(id=eid, videoInfoId=eid - 5000, parentId=title_id, url=SHARE + rel, dur=dur,
                       title=title, playPoint=0, lastWatchTime=-1)
            self.entries[eid] = rec
            self.by_url[rec["url"].lower()] = rec
        # Decoy entry whose id equals another player's videoInfoId range, to prove the guard works.
        self.writes = []

    def play_path(self, url):
        # smb://10.0.0.56/Entertainment/... -> /data/system/smb/10.0.0.56#Entertainment/...
        rest = url[len("smb://"):]
        host, _, tail = rest.partition("/")
        return f"/data/system/smb/{host}#{tail}"


PLAYERS = {}


def make_handler():
    class H(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def send(self, obj, code=200):
            body = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            host = self.server.server_address[0]
            p = PLAYERS[host]
            u = urlparse(self.path)
            q = {k: v[0] for k, v in parse_qs(u.query, keep_blank_values=True).items()}
            path = u.path

            if path.startswith("/_test/"):
                return self.test(p, path, q)
            if p.offline:
                # A sleeping player simply doesn't answer.
                time.sleep(5)
                self.close_connection = True
                return

            with p.lock:
                if p.ht_closed and path.startswith("/ZidooPoster"):
                    # Player is on but the Home Theater app is closed: the library API doesn't answer usefully.
                    return self.send(dict(status=806, msg="The resource does not exist"))
                if path == "/ZidooControlCenter/getModel":
                    return self.send(dict(status=200, model="Z9X PRO", ip=p.ip, net_mac=p.mac, wif_mac="",
                                          firmware="v1.0.95_G", duuid="uuid-" + p.mac))
                if path == "/ZidooVideoPlay/getPlayStatus":
                    if not p.playing:
                        return self.send(dict(status=806, msg="The resource does not exist"))
                    pl = p.playing
                    return self.send(dict(status=200, video=dict(status=1, title=pl["title"], path=pl["path"],
                                                                  currentPosition=pl["pos"], duration=pl["dur"])))
                if path == "/ZidooPoster/v2/getIdByPathname":
                    rec = p.by_url.get(q.get("path", "").lower())
                    if not rec:
                        return self.send(dict(status=404, code="NOT_FOUND", msg="not found"))
                    # Real players: "id" is the TITLE for a matched file; the file's own entry is "fileId".
                    return self.send(dict(status=200, code="OK", msg="success", id=rec["parentId"], type=1,
                                          fileId=rec["id"], videoInfoId=rec["videoInfoId"], matched=True))
                if path == "/ZidooPoster/v2/getEntry":
                    eid = int(q.get("id", 0))
                    rec = p.entries.get(eid)
                    kids = [r for r in p.entries.values() if r["parentId"] == eid]
                    if not rec and kids:
                        # A title entry: no path of its own, files listed as children (as seen for Braveheart 1310).
                        return self.send(dict(status=200, code="OK", msg="success", id=eid, parentId=-1, type=1,
                                              name=kids[0]["title"], path="", playPoint=0,
                                              children=[dict(id=k["id"], path=k["url"]) for k in kids]))
                    if not rec:
                        return self.send(dict(status=404, code="NOT_FOUND", msg="not found"))
                    return self.send(dict(status=200, code="OK", msg="success", id=rec["id"], metadataId=rec["videoInfoId"],
                                          parentId=rec["parentId"], type=0, name=rec["url"].rsplit("/", 1)[1],
                                          path=rec["url"], videoInfoId=rec["videoInfoId"], playPoint=rec["playPoint"], state=3))
                if path == "/ZidooPoster/getDetail":
                    tid = int(q.get("id", 0))
                    rows = [r for r in p.entries.values() if r["parentId"] == tid]
                    if not rows:
                        return self.send(dict(status=804, msg="Error!!!"))
                    return self.send(dict(id=tid, type=1, name=rows[0]["title"], aggregations=[
                        dict(id=r["id"], parentId=tid, aggregationId=r["videoInfoId"], type=0, name=r["url"].rsplit("/", 1)[1],
                             aggregation=dict(id=r["videoInfoId"], playPoint=r["playPoint"], lastWatchTime=r["lastWatchTime"],
                                              duration=r["dur"], uri=r["url"][len(SHARE):]))
                        for r in rows]))
                if path == "/ZidooPoster/v2/setPlayPoint":
                    eid = int(q.get("id", 0))
                    rec = p.entries.get(eid)
                    p.writes.append(dict(q))
                    if not rec:
                        return self.send(dict(status=200, msg="success"))  # real players can say success for a bad id
                    pp = int(q.get("playPoint", 0))
                    rec["playPoint"] = pp
                    rec["lastWatchTime"] = int(q["lastWatchTime"]) if q.get("lastWatchTime") else now_ms()
                    if p.buggy_write:
                        return self.send(dict(status=500, msg="error"))  # HT 5.1.11 / 1619B: error, but it worked
                    return self.send(dict(status=200, msg="success"))
                return self.send(dict(status=804, msg="Url error"))

        def test(self, p, path, q):
            with p.lock:
                if path == "/_test/play":
                    url = q["url"]
                    rec = p.by_url[url.lower()]
                    p.playing = dict(path=p.play_path(url), pos=int(q["pos"]), dur=rec["dur"], title=rec["title"], url=url)
                    return self.send(dict(ok=True))
                if path == "/_test/stop":
                    if p.playing:
                        rec = p.by_url[p.playing["url"].lower()]
                        rec["playPoint"] = p.playing["pos"]
                        rec["lastWatchTime"] = now_ms()
                    p.playing = None
                    return self.send(dict(ok=True))
                if path == "/_test/offline":
                    p.offline = q.get("on") == "1"
                    return self.send(dict(ok=True))
                if path == "/_test/state":
                    rec = p.by_url.get(q["url"].lower())
                    return self.send(dict(found=bool(rec), playPoint=rec["playPoint"] if rec else None,
                                          lastWatchTime=rec["lastWatchTime"] if rec else None, writes=p.writes))
                if path == "/_test/htclosed":
                    p.ht_closed = q.get("on") == "1"
                    return self.send(dict(ok=True))
                if path == "/_test/setmac":
                    p.mac = q["mac"]
                    return self.send(dict(ok=True))
                if path == "/_test/setlwt":
                    rec = p.by_url[q["url"].lower()]
                    rec["lastWatchTime"] = int(q["lwt"]); rec["playPoint"] = int(q["pp"])
                    return self.send(dict(ok=True))
            return self.send(dict(error="unknown"), 404)
    return H


def main():
    specs = [
        Player("Living Room", "127.0.0.2", "80:0a:80:55:d7:b0", 24000),
        Player("Bedroom", "127.0.0.3", "80:0a:80:5c:db:aa", 18000, buggy_write=True),
        Player("Home Theater Room", "127.0.0.4", "80:0a:80:5c:da:26", 31000, has_files={0, 2, 3}),  # no Arrival, no featurette
    ]
    for pl in specs:
        PLAYERS[pl.ip] = pl
        srv = ThreadingHTTPServer((pl.ip, 9529), make_handler())
        threading.Thread(target=srv.serve_forever, daemon=True).start()
    print("mock players up", flush=True)
    while True:
        time.sleep(3600)


if __name__ == "__main__":
    main()
