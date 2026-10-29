"""A stand-in Plex Media Server (127.0.0.6:32400) and plex.tv (127.0.0.7:32400), started by mock_zidoo.py.

Plex sees the share at /shares/Entertainment (the Zidoos see smb://10.0.0.56/Entertainment). Two Plex Home users:
the owner "chet" (account 1001; shown as user "1" in the server's sessions) and "Kid" (account 1002, PIN 1234).

Test hooks on the server:
  /_test/play?user=1001&key=<ratingKey>&pos=<ms>&state=playing|paused   a session for that user
  /_test/stop?user=1001[&final=<ms>]       the session ends; the app saves the position (or marks it played at >= 90%)
  /_test/item?user=1001&key=<ratingKey>    that user's state of an item, plus every write made
  /_test/keys                              ratingKey of each file, by file name
Hook on plex.tv:
  /_test/link                              the user entered the sign-in code at plex.tv/link
"""
import json, threading, time
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs

SERVER_IP, TV_IP, PORT = "127.0.0.6", "127.0.0.7", 32400
MACHINE = "plexmachine0001"
PLEX_ROOT = "/shares/Entertainment"
# account id -> (account token, server token, title, uuid)
ACCOUNTS = {"1001": ("acct-owner", "srv-owner", "chet", "uuid-chet"), "1002": ("acct-kid", "srv-kid", "Kid", "uuid-kid")}
KID_PIN = "1234"

lock = threading.Lock()
ITEMS = {}      # ratingKey -> dict(type, title, show, season, episode, year, file, duration, showKey)
STATE = {}      # (user, ratingKey) -> dict(viewOffset, viewCount, lastViewedAt)
SESSIONS = {}   # user -> dict(key, pos, state)
WRITES = []
PNG = {"data": b""}
LINKED = {"done": False}


def setup(files):
    """files: list of (share-relative path, duration ms, title) — the same library as the Zidoos (no extras)."""
    import re
    show_keys = {}
    k = 100
    movie_key = {}
    for rel, dur, title in files:
        k += 1
        if "/Featurettes/" in rel:
            # An extra: its own item with its own resume point, but listed under its movie, not in the library.
            ITEMS[k] = dict(type="clip", title=rel.rsplit("/", 1)[1], show="", season=0, episode=0, year=1995,
                            file=PLEX_ROOT + rel, duration=dur, showKey=0, parent=movie_key.get(title, 0))
            continue
        m = re.search(r"S(\d+)E(\d+)", rel)
        if m:
            sk = show_keys.setdefault(title, 900 + len(show_keys))
            ITEMS[k] = dict(type="episode", title=f"Episode {int(m.group(2))}", show=title, season=int(m.group(1)),
                            episode=int(m.group(2)), year=2022, file=PLEX_ROOT + rel, duration=dur, showKey=sk)
        else:
            movie_key[title] = k
            ITEMS[k] = dict(type="movie", title=title, show="", season=0, episode=0, year=2016,
                            file=PLEX_ROOT + rel, duration=dur, showKey=0)


def st(user, key):
    return STATE.setdefault((user, key), dict(viewOffset=0, viewCount=0, lastViewedAt=0))


def meta(user, key):
    it, s = ITEMS[key], st(user, key)
    m = dict(ratingKey=str(key), key=f"/library/metadata/{key}", type=it["type"], title=it["title"], year=it["year"],
             duration=it["duration"], thumb=f"/library/metadata/{key}/thumb/1",
             Media=[dict(Part=[dict(file=it["file"], duration=it["duration"])])])
    if it["type"] == "episode":
        m.update(grandparentTitle=it["show"], parentIndex=it["season"], index=it["episode"],
                 grandparentRatingKey=str(it["showKey"]), parentThumb=f"/library/metadata/{it['showKey']}1/thumb/1",
                 grandparentThumb=f"/library/metadata/{it['showKey']}/thumb/1")
    if s["viewOffset"] > 0:
        m["viewOffset"] = s["viewOffset"]
    if s["viewCount"] > 0:
        m["viewCount"] = s["viewCount"]
    if s["lastViewedAt"] > 0:
        m["lastViewedAt"] = s["lastViewedAt"]
    return m


class Base(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def send(self, obj, code=200):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def parts(self):
        u = urlparse(self.path)
        return u.path, {k: v[0] for k, v in parse_qs(u.query, keep_blank_values=True).items()}

    def token(self, q):
        return self.headers.get("X-Plex-Token") or q.get("X-Plex-Token", "")


class Server(Base):
    def user_of(self, tok):
        for uid, (_, srv, _, _) in ACCOUNTS.items():
            if tok == srv:
                return uid
        return None

    def do_GET(self):
        path, q = self.parts()
        if path.startswith("/_test/"):
            return self.test(path, q)
        if path == "/identity":
            return self.send(dict(MediaContainer=dict(machineIdentifier=MACHINE, version="1.41.0")))
        user = self.user_of(self.token(q))
        if user is None:
            return self.send(dict(error="unauthorized"), 401)
        with lock:
            if path == "/status/sessions":
                if user != "1001":
                    return self.send(dict(error="admin only"), 401)
                md = []
                for uid, s in SESSIONS.items():
                    m = meta(uid, s["key"])
                    m["viewOffset"] = s["pos"]
                    m["User"] = dict(id=s.get("uid") or ("1" if uid == "1001" else uid), title=s.get("utitle") or ACCOUNTS[uid][2])
                    if s.get("nofile"):
                        for med in m["Media"]:
                            for part in med["Part"]:
                                part.pop("file", None)
                    m["Player"] = dict(state=s["state"], title="Plex for Android (TV)")
                    md.append(m)
                return self.send(dict(MediaContainer=dict(size=len(md), Metadata=md)))
            if path == "/library/sections":
                return self.send(dict(MediaContainer=dict(Directory=[
                    dict(key="1", type="movie", title="Movies", Location=[dict(path=PLEX_ROOT + "/Movies")]),
                    dict(key="2", type="show", title="TV Shows", Location=[dict(path=PLEX_ROOT + "/TV"), dict(path="smb://10.0.0.56/Entertainment/TV")]),
                ])))
            if path.startswith("/library/sections/") and path.endswith("/all"):
                sec = path.split("/")[3]
                want = "movie" if sec == "1" else "episode"
                return self.send(dict(MediaContainer=dict(Metadata=[meta(user, k) for k, it in sorted(ITEMS.items()) if it["type"] == want])))
            if path.startswith("/library/metadata/"):
                bits = path.split("/")
                key = int(bits[3])
                if len(bits) > 4 and bits[4] == "extras":
                    return self.send(dict(MediaContainer=dict(Metadata=[dict(meta(user, k), extraType=5, subtype="behindTheScenes")
                                                                        for k, it in sorted(ITEMS.items()) if it.get("parent") == key])))
                if len(bits) > 4 and bits[4] == "allLeaves":
                    return self.send(dict(MediaContainer=dict(Metadata=[meta(user, k) for k, it in sorted(ITEMS.items()) if it["showKey"] == key])))
                if len(bits) > 4 and bits[4] == "thumb":
                    return self.png()
                if key not in ITEMS:
                    return self.send(dict(error="not found"), 404)
                return self.send(dict(MediaContainer=dict(Metadata=[meta(user, key)])))
            if path == "/photo/:/transcode":
                return self.png()
            if path in ("/:/progress", "/:/scrobble", "/:/unscrobble"):
                key = int(q["key"])
                keys = [key] if key in ITEMS else [k for k, it in ITEMS.items() if it["showKey"] == key]
                WRITES.append(dict(user=user, path=path, key=key, time=q.get("time")))
                now = int(time.time())
                for k in keys:
                    s = st(user, k)
                    if path == "/:/progress":
                        s["viewOffset"] = int(q.get("time", "0"))
                        s["lastViewedAt"] = now
                    elif path == "/:/scrobble":
                        s.update(viewOffset=0, viewCount=s["viewCount"] + 1, lastViewedAt=now)
                    else:
                        s.update(viewOffset=0, viewCount=0)
                return self.send({})
        return self.send(dict(error="unknown " + path), 404)

    do_PUT = do_GET

    def png(self):
        data = PNG["data"]
        self.send_response(200)
        self.send_header("Content-Type", "image/png")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def test(self, path, q):
        with lock:
            if path == "/_test/play":
                SESSIONS[q["user"]] = dict(key=int(q["key"]), pos=int(q["pos"]), state=q.get("state", "playing"),
                                           uid=q.get("uid"), utitle=q.get("utitle"), nofile=q.get("nofile"))
                return self.send({"ok": True})
            if path == "/_test/stop":
                s = SESSIONS.pop(q["user"], None)
                if s:
                    pos = int(q.get("final", s["pos"]))
                    it, rec = ITEMS[s["key"]], st(q["user"], s["key"])
                    if pos >= it["duration"] * 0.9:
                        rec.update(viewOffset=0, viewCount=rec["viewCount"] + 1, lastViewedAt=int(time.time()))
                    else:
                        rec.update(viewOffset=pos, lastViewedAt=int(time.time()))
                return self.send({"ok": True})
            if path == "/_test/item":
                key = int(q["key"])
                return self.send(dict(st(q["user"], key), writes=[w for w in WRITES if w["user"] == q["user"] and w["key"] in (key, ITEMS[key]["showKey"])]))
            if path == "/_test/keys":
                return self.send({it["file"].rsplit("/", 1)[1]: k for k, it in ITEMS.items()})
            if path == "/_test/set":
                st(q["user"], int(q["key"])).update(viewOffset=int(q.get("pos", 0)), viewCount=int(q.get("count", 0)), lastViewedAt=int(q.get("at", time.time())))
                return self.send({"ok": True})
        return self.send({"error": "unknown"}, 404)


class Tv(Base):
    def acct(self, q):
        tok = self.token(q)
        for uid, (acct, _, _, _) in ACCOUNTS.items():
            if tok == acct:
                return uid
        return None

    def do_GET(self):
        path, q = self.parts()
        if path == "/_test/link":
            LINKED["done"] = True
            return self.send({"ok": True})
        if path == "/repos/ckenisell/resume-relay/releases":   # stands in for GitHub's release list (update notice)
            return self.send([
                dict(tag_name="v9.9.1-beta.1", prerelease=True, draft=False, html_url="https://github.com/x/beta", body="## Beta\n- **New** thing", published_at="2026-11-02T10:00:00Z"),
                dict(tag_name="v9.9.0", prerelease=False, draft=False, html_url="https://github.com/x/full", body="## What's new\n- Big things", published_at="2026-11-01T10:00:00Z"),
                dict(tag_name="v0.1.0", prerelease=False, draft=False, html_url="https://github.com/x/old", body="old", published_at="2026-01-01T10:00:00Z"),
            ])
        if path.startswith("/api/v2/pins/"):
            return self.send(dict(id=42, code="RRPX", authToken="acct-owner" if LINKED["done"] else None))
        uid = self.acct(q)
        if uid is None:
            return self.send(dict(error="unauthorized"), 401)
        if path == "/api/v2/user":
            _, _, title, uuid = ACCOUNTS[uid]
            return self.send(dict(id=int(uid), uuid=uuid, username=title, title=title))
        if path == "/api/v2/resources":
            return self.send([
                dict(name="Some Player", clientIdentifier="player1", provides="client,player", connections=[]),
                dict(name="CHETPLEX", clientIdentifier=MACHINE, provides="server", owned=uid == "1001",
                     accessToken=ACCOUNTS[uid][1], connections=[
                         dict(uri="https://10-9-9-9.abc.plex.direct:32400", local=False, relay=True),
                         dict(uri=f"http://{SERVER_IP}:{PORT}", local=True, relay=False),
                     ]),
            ])
        if path == "/api/v2/home/users":
            if uid != "1001":
                return self.send(dict(error="not admin"), 403)
            return self.send(dict(users=[
                dict(id=1001, uuid="uuid-chet", title="chet", admin=True, protected=False),
                dict(id=1002, uuid="uuid-kid", title="Kid", admin=False, protected=True),
            ]))
        return self.send(dict(error="unknown " + path), 404)

    def do_POST(self):
        path, q = self.parts()
        n = int(self.headers.get("Content-Length") or 0)
        if n:
            self.rfile.read(n)
        if path == "/api/v2/pins":
            LINKED["done"] = False
            return self.send(dict(id=42, code="RRPX"), 201)
        if path.startswith("/api/v2/home/users/") and path.endswith("/switch"):
            if self.acct(q) != "1001":
                return self.send(dict(error="unauthorized"), 401)
            uuid = path.split("/")[5]
            for uid, (acct, _, _, u) in ACCOUNTS.items():
                if u == uuid:
                    if uid == "1002" and q.get("pin") != KID_PIN:
                        return self.send(dict(error="bad pin"), 403)
                    return self.send(dict(authToken=acct), 201)
            return self.send(dict(error="no user"), 404)
        return self.send(dict(error="unknown " + path), 404)


def start(files, png):
    PNG["data"] = png
    setup(files)
    for ip, handler in ((SERVER_IP, Server), (TV_IP, Tv)):
        srv = ThreadingHTTPServer((ip, PORT), handler)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
