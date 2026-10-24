"""Simulated Zidoo players for testing Resume Relay.

Each player listens on its own loopback IP, port 9529, and answers the same endpoints, in the
same shapes, as the real responses captured with Z-Keeper's API Probe.

Library shape (as on real players): a film's files hang under the film entry (type 1); an episode's
file hangs under its season (type 4), which hangs under the series (type 3). getIdByPathname's "id"
is that parent; the file's own entry is "fileId".

Test-only control endpoints (not on real players):
  /_test/play?url=<source url>&pos=<ms>     start/advance playback (switching file saves the old one's bookmark)
  /_test/stop[?final=<ms>&delay=<ms>]       stop; the player saves its bookmark (at final, after delay)
  /_test/offline?on=1|0                     simulate asleep/off
  /_test/htclosed?on=1|0                    player on, Home Theater app closed
  /_test/state?url=<source url>             read bookmark directly (+ writes, poster requests, power log)
  /_test/setmac?mac=..                      pretend a different player took this IP
  /_test/setlwt?url=..&lwt=..&pp=..         set a bookmark directly

Wake-on-LAN: a UDP listener on MOCK_WOL_PORT (default 40009) wakes an "off" player whose MAC is in the
magic packet. Like a real player, it comes up with its Home Theater app closed.
"""
import json, os, re, socket, struct, sys, threading, time, zlib
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
WOL_PORT = int(os.environ.get("MOCK_WOL_PORT", "40009"))


def now_ms():
    return int(time.time() * 1000)


def tiny_png(seed):
    """A valid 2x3 PNG, different per seed (a stand-in poster)."""
    raw = b"".join(b"\x00" + bytes([(seed * 37) % 256, (seed * 91) % 256, 120]) * 2 for _ in range(3))
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 2, 3, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


class Player:
    def __init__(self, name, ip, mac, id_base, buggy_write=False, has_files=None):
        self.name, self.ip, self.mac = name, ip, mac
        self.buggy_write = buggy_write
        self.offline = False
        self.ht_closed = False
        self.ht_version, self.ht_code = "5.1.11", 5111
        self.flaky_every, self.detail_calls, self.fail_ids = 0, 0, set()
        self.playing = None
        self.lock = threading.Lock()
        self.entries = {}   # file entry id -> record
        self.titles = {}    # film / season / series entry id -> dict(type, parentId, name)
        self.by_url = {}
        self.writes, self.poster_requests, self.power_log = [], [], []
        next_title = [id_base + 5000]

        def title(kind, name, parent=-1):
            for tid, t in self.titles.items():
                if t["type"] == kind and t["name"] == name and t["parentId"] == parent:
                    return tid
            tid = next_title[0]; next_title[0] += 1
            self.titles[tid] = dict(type=kind, parentId=parent, name=name)
            return tid

        for i, (rel, dur, show) in enumerate(FILES):
            if has_files is not None and i not in has_files:
                continue
            m = re.search(r"S(\d+)E(\d+)", rel)
            if m:
                series = title(3, show)
                parent = title(4, f"{show} Season {int(m.group(1))}", series)
                season, episode = int(m.group(1)), int(m.group(2))
            else:
                parent, season, episode = title(1, show), None, None
            eid = id_base + i * 7          # different numbering on every player
            rec = dict(id=eid, videoInfoId=eid - 5000, parentId=parent, url=SHARE + rel, rel=rel, dur=dur, title=show,
                       playPoint=0, lastWatchTime=-1, season=season, episode=episode)
            self.entries[eid] = rec
            self.by_url[rec["url"].lower()] = rec
        # A movie collection, so the library reader has to open collections: Arrival sits inside one.
        self.collections = {}
        for tid, t in self.titles.items():
            if t["type"] == 1 and t["name"] == "Arrival":
                self.collections[id_base + 6000] = dict(name="Sci-Fi Collection", members=[tid])

    def top_tiles(self):
        """The poster wall: movies not in a collection, collections, series (as getVideoList lists them)."""
        in_coll = {m for c in self.collections.values() for m in c["members"]}
        rows = [dict(id=tid, parentId=t["parentId"], collectionId=tid if t["type"] == 3 else -1, aggregationId=tid + 1,
                     type=t["type"], name=t["name"], year=2016)
                for tid, t in sorted(self.titles.items()) if t["type"] in (1, 3) and tid not in in_coll]
        # Real players open a collection by the tile's own id; collectionId here points at something else
        # (the series), the trap the 0.3.3-beta.3 reader fell into.
        series = next((tid for tid, t in self.titles.items() if t["type"] == 3), -1)
        rows += [dict(id=cid, parentId=-1, collectionId=series, aggregationId=-1, type=2, name=c["name"])
                 for cid, c in sorted(self.collections.items())]
        return rows

    def file_row(self, r):
        return dict(id=r["id"], type=0, name=r["rel"].rsplit("/", 1)[1], duration=r["dur"],
                    watched=bool(r.get("watched")),
                    aggregation=dict(id=r["id"] + 50000, uri=r["rel"], playPoint=r["playPoint"],
                                     lastWatchTime=r["lastWatchTime"], addedTime=1790288635484))

    def detail(self, tid):
        """getDetail for a movie (files) or a season (episodes → files), shaped like the HT5 API guide's examples."""
        t = self.titles[tid]
        kids = sorted((r for r in self.entries.values() if r["parentId"] == tid), key=lambda r: r["id"])
        if t["type"] == 1:
            return dict(status=200, id=tid, type=1, name=t["name"], year=1995, watched=False, position=0,
                        duration=kids[0]["dur"] if kids else 0,
                        aggregation=dict(aggregations=[self.file_row(r) for r in kids]))
        eps = [dict(id=r["id"] + 90000, type=5, name=f"Episode {r['episode']}", position=r["playPoint"],
                    aggregation=dict(seasonNumber=r["season"], episodeNumber=r["episode"]),
                    aggregations=[self.file_row(r)]) for r in kids]
        return dict(status=200, id=tid, type=4, name=t["name"], aggregation=dict(aggregations=eps),
                    currentWatchSeasonNumber=1, currentWatchEpisodeNumber=1)

    def play_path(self, url):
        # smb://10.0.0.56/Entertainment/... -> /data/system/smb/10.0.0.56#Entertainment/...
        rest = url[len("smb://"):]
        host, _, tail = rest.partition("/")
        return f"/data/system/smb/{host}#{tail}"

    def save_bookmark(self, url, pos):
        rec = self.by_url[url.lower()]
        rec["playPoint"] = pos
        rec["lastWatchTime"] = now_ms()


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

        def send_png(self, data):
            self.send_response(200)
            self.send_header("Content-Type", "image/png")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

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
                if p.ht_closed and path.startswith(("/ZidooPoster", "/Poster")):
                    # Player is on but the Home Theater app is closed: the library API doesn't answer usefully.
                    return self.send(dict(status=806, msg="The resource does not exist"))
                if path == "/ZidooControlCenter/getModel":
                    return self.send(dict(status=200, model="Z9X PRO", ip=p.ip, net_mac=p.mac, wif_mac="",
                                          firmware="v1.0.95_G", duuid="uuid-" + p.mac))
                if path == "/ZidooControlCenter/Apps/getApps":
                    return self.send(dict(status=200, apps=[
                        dict(label="Settings", packageName="com.android.settings", versionName="9", versionCode=28),
                        dict(label="Home Theater", packageName="com.zidoo.poster", versionName=p.ht_version, versionCode=p.ht_code)]))
                if path == "/ZidooControlCenter/Apps/openApp":
                    p.power_log.append("openApp")
                    p.ht_closed = False
                    return self.send(dict(status=200, msg="success"))
                if path == "/ZidooControlCenter/RemoteControl/sendkey":
                    if q.get("key") == "Key.PowerOn.Poweroff":
                        p.power_log.append("poweroff")
                        p.playing = None
                        threading.Timer(0.5, lambda: setattr(p, "offline", True)).start()
                    return self.send(dict(status=200, msg="success"))
                if path == "/ZidooVideoPlay/getPlayStatus":
                    if not p.playing:
                        return self.send(dict(status=806, msg="The resource does not exist"))
                    pl = p.playing
                    return self.send(dict(status=200, video=dict(status=1, title=pl["title"], path=pl["path"],
                                                                  currentPosition=pl["pos"], duration=pl["dur"])))
                if path == "/ZidooPoster/getVideoList":
                    page, size = int(q.get("page", 1)), int(q.get("pagesize", 50))
                    rows = p.top_tiles()[(page - 1) * size: page * size]
                    return self.send(dict(status=200, page=page, pagesize=size, size=len(rows), data=rows))
                if path == "/ZidooPoster/getCollection":
                    cid = int(q.get("id", 0))
                    if cid in p.collections:
                        rows = [dict(id=m, parentId=-1, collectionId=-1, type=1, name=p.titles[m]["name"]) for m in p.collections[cid]["members"]]
                    elif cid in p.titles and p.titles[cid]["type"] == 3:
                        rows = [dict(id=k, parentId=cid, collectionId=-1, type=4, name=t["name"]) for k, t in sorted(p.titles.items()) if t["parentId"] == cid]
                    else:
                        return self.send(dict(status=804, msg="Url error"))
                    return self.send(dict(status=200, data=rows))
                if path in ("/ZidooPoster/getDetail", "/Poster/v2/getDetail") and (p.flaky_every or int(q.get("id", 0)) in p.fail_ids):
                    p.detail_calls += 1
                    if int(q.get("id", 0)) in p.fail_ids or (p.flaky_every and p.detail_calls % p.flaky_every != 0):
                        return self.send(dict(status=500, msg="busy"))   # a player too busy to answer properly
                if path == "/ZidooPoster/getDetail":
                    tid = int(q.get("id", 0))
                    if tid in p.titles and p.titles[tid]["type"] in (1, 4):
                        return self.send(p.detail(tid))
                    return self.send(dict(status=803, msg="not a movie or season"))
                if path == "/ZidooPoster/v2/getPoster":
                    tid = int(q.get("id", 0))
                    p.poster_requests.append(tid)
                    if tid in p.titles:
                        return self.send_png(tiny_png(tid))
                    return self.send(dict(status=404, msg="not found"))
                if path == "/ZidooPoster/v2/getIdByPathname":
                    rec = p.by_url.get(q.get("path", "").lower())
                    if not rec:
                        return self.send(dict(status=404, code="NOT_FOUND", msg="not found"))
                    # Real players: "id" is the parent (the film for a matched file); the file's own entry is "fileId".
                    return self.send(dict(status=200, code="OK", msg="success", id=rec["parentId"], type=1,
                                          fileId=rec["id"], videoInfoId=rec["videoInfoId"], matched=True))
                if path == "/ZidooPoster/v2/getEntry":
                    eid = int(q.get("id", 0))
                    if eid in p.titles:
                        t = p.titles[eid]
                        kids = [r for r in p.entries.values() if r["parentId"] == eid]
                        return self.send(dict(status=200, code="OK", msg="success", id=eid, parentId=t["parentId"],
                                              type=t["type"], name=t["name"], path="", playPoint=0,
                                              children=[dict(id=k["id"], type=0, path=k["url"], playPoint=k["playPoint"]) for k in kids]
                                              + [dict(id=c, type=ct["type"], name=ct["name"]) for c, ct in sorted(p.titles.items()) if ct["parentId"] == eid]))
                    rec = p.entries.get(eid)
                    if not rec:
                        return self.send(dict(status=404, code="NOT_FOUND", msg="not found"))
                    return self.send(dict(status=200, code="OK", msg="success", id=rec["id"], metadataId=rec["videoInfoId"],
                                          parentId=rec["parentId"], type=0, name=rec["url"].rsplit("/", 1)[1],
                                          path=rec["url"], videoInfoId=rec["videoInfoId"], playPoint=rec["playPoint"],
                                          seasonNumber=rec["season"], episodeNumber=rec["episode"], state=3))
                if path == "/Poster/v2/markAsWatched":
                    rec = p.entries.get(int(q.get("aggregationId", 0)))
                    p.writes.append(dict(q, call="markAsWatched"))
                    if rec and q.get("watched") == "1":
                        rec["watched"] = True
                        rec["playPoint"] = -max(1, rec["playPoint"])   # parks the resume point as a negative value
                        rec["lastWatchTime"] = now_ms()
                    return self.send(dict(status=200, msg="success"))
                if path == "/ZidooPoster/v2/setPlayPoint":
                    eid = int(q.get("id", 0))
                    rec = p.entries.get(eid)
                    p.writes.append(dict(q))
                    if not rec:
                        return self.send(dict(status=200, msg="success"))  # real players can say success for a bad id
                    rec["playPoint"] = int(q.get("playPoint", 0))
                    pp, dur = rec["playPoint"], rec["dur"]
                    rec["watched"] = pp > 0 and (pp >= dur * 0.93 or dur - pp <= 300000)  # short of the end clears watched
                    if rec["playPoint"] == 0:
                        rec["lastWatchTime"] = -1      # playPoint=0 erases the bookmark (and watched mark)
                        rec["watched"] = False
                    else:
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
                    if p.playing and p.playing["url"].lower() != url.lower():
                        p.save_bookmark(p.playing["url"], p.playing["pos"])   # auto-play saves the file it leaves
                    p.playing = dict(path=p.play_path(url), pos=int(q["pos"]), dur=rec["dur"], title=rec["title"], url=url)
                    return self.send(dict(ok=True))
                if path == "/_test/stop":
                    if p.playing:
                        url, pos = p.playing["url"], int(q.get("final", p.playing["pos"]))
                        delay = int(q.get("delay", 0)) / 1000
                        if delay > 0:
                            threading.Timer(delay, lambda: p.save_bookmark(url, pos)).start()
                        else:
                            p.save_bookmark(url, pos)
                    p.playing = None
                    return self.send(dict(ok=True))
                if path == "/_test/offline":
                    p.offline = q.get("on") == "1"
                    return self.send(dict(ok=True))
                if path == "/_test/state":
                    rec = p.by_url.get(q.get("url", "").lower())
                    return self.send(dict(found=bool(rec), playPoint=rec["playPoint"] if rec else None, watched=bool(rec and rec.get("watched")),
                                          lastWatchTime=rec["lastWatchTime"] if rec else None, writes=p.writes,
                                          posterRequests=p.poster_requests, powerLog=p.power_log,
                                          offline=p.offline, htClosed=p.ht_closed,
                                          titles={str(k): v for k, v in p.titles.items()}))
                if path == "/_test/flaky":
                    p.flaky_every = int(q.get("every", 0)); p.detail_calls = 0
                    p.fail_ids = {int(x) for x in q.get("fail", "").split(",") if x}
                    return self.send(dict(ok=True))
                if path == "/_test/htclosed":
                    p.ht_closed = q.get("on") == "1"
                    return self.send(dict(ok=True))
                if path == "/_test/setmac":
                    p.mac = q["mac"]
                    return self.send(dict(ok=True))
                if path == "/_test/setlwt":
                    rec = p.by_url[q["url"].lower()]
                    rec["lastWatchTime"] = int(q["lwt"]); rec["playPoint"] = int(q["pp"])
                    if "watched" in q:
                        rec["watched"] = q["watched"] == "1"
                    return self.send(dict(ok=True))
            return self.send(dict(error="unknown"), 404)
    return H


def wol_listener():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("0.0.0.0", WOL_PORT))
    while True:
        data, _ = s.recvfrom(1024)
        if len(data) < 102 or data[:6] != b"\xff" * 6:
            continue
        mac = ":".join(f"{b:02x}" for b in data[6:12])
        for p in PLAYERS.values():
            if p.mac.lower() == mac and p.offline:
                p.power_log.append("wol")
                def boot(pl=p):
                    pl.ht_closed = True        # comes up on the Android home screen
                    pl.offline = False
                threading.Timer(2.0, boot).start()


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
    threading.Thread(target=wol_listener, daemon=True).start()
    print("mock players up", flush=True)
    while True:
        time.sleep(3600)


if __name__ == "__main__":
    main()
