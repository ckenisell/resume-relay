"""A stand-in Jellyfin server (127.0.0.8:8096, and discovery on UDP 127.0.0.8:7359), started by mock_zidoo.py.

It sees the share at /shares/Entertainment, like Plex. Users: "chet" (administrator, password "pw") and "Wife"
(password "pw2", not an administrator). An API key "apikey-0123456789abcdef" works as an administrator. Quick Connect
is on; /_test/approve approves the code. Extras (Featurettes) aren't in the library, as in Jellyfin's item listing.

Test hooks:
  /_test/play?user=<id>&id=<item id>&pos=<ms>[&paused=1]   a session for that user
  /_test/stop?user=<id>[&final=<ms>]       the session ends; the position is saved (played, no position, at >= 90%)
  /_test/item?user=<id>&id=<item id>       that user's state of an item, plus every write made to it
  /_test/ids                               item id of each file, by file name
  /_test/set?user=<id>&id=<item id>&pos=<ms>&played=0|1
  /_test/approve                           the Quick Connect code was approved by "chet"
  /_test/root?to=/data/media               Jellyfin sees the share under another folder from now on
"""
import json, re, socket, threading, time
from datetime import datetime, timezone
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from urllib.parse import urlparse, parse_qs

IP, PORT = "127.0.0.8", 8096
SERVER_ID, SERVER_NAME = "jfserver0000000000000000000000001", "CHETJF"
ROOT = "/shares/Entertainment"
CURRENT = {"root": ROOT}   # where Jellyfin sees the share now (/_test/root changes it)
USERS = {"u-chet": ("chet", "pw", True, "tok-chet"), "u-wife": ("Wife", "pw2", False, "tok-wife")}
API_KEY = "apikey-0123456789abcdef"
TICKS = 10_000

lock = threading.Lock()
ITEMS = {}      # id -> dict(type, name, series, seriesId, seasonId, season, episode, year, path, ms)
STATE = {}      # (user, id) -> dict(pos ms, played bool, count, last unix)
SESSIONS = {}   # user -> dict(id, pos, paused)
WRITES = []
PNG = {"data": b""}
QUICK = {"approved": False}
RATINGS = {"Braveheart": 8.3, "Arrival": 7.9, "Andor": 8.4}
# A movie only Jellyfin rates (the Zidoos and Plex don't have it): Highly Acclaimed takes Jellyfin's rating for it.
ONLY_HERE = dict(type="Movie", name="Heat", year=1995, path=ROOT + "/Movies/Heat (1995)/Heat (1995).mkv", rating=8.3)


def gid(n):
    return f"{n:032x}"


def setup(files):
    series = {}
    n = 0x1000
    for rel, dur, title in files:
        if "/Featurettes/" in rel:
            continue
        n += 1
        m = re.search(r"S(\d+)E(\d+)", rel)
        if m:
            sid = series.setdefault(title, gid(0x9000 + len(series)))
            ITEMS[gid(n)] = dict(type="Episode", name=f"Episode {int(m.group(2))}", series=title, seriesId=sid, seasonId=gid(0x9100 + int(m.group(1))),
                                 season=int(m.group(1)), episode=int(m.group(2)), year=2022, path=ROOT + rel, ms=dur)
        else:
            ITEMS[gid(n)] = dict(type="Movie", name=title, series="", seriesId="", seasonId="", season=0, episode=0, year=2016, path=ROOT + rel, ms=dur)
    for title, sid in series.items():
        ITEMS[sid] = dict(type="Series", name=title, series="", seriesId="", seasonId="", season=0, episode=0, year=2022, path=ROOT + "/TV/" + title, ms=0)


def st(user, iid):
    return STATE.setdefault((user, iid), dict(pos=0, played=False, count=0, last=0))


def iso(t):
    return datetime.fromtimestamp(t, timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.0000000Z") if t else None


def dto(iid, user):
    it = ITEMS[iid]
    d = dict(Id=iid, Name=it["name"], Type=it["type"], ProductionYear=it["year"], Path=it["path"],
             ImageTags=dict(Primary="tag" + iid[-4:]))
    if it["ms"]:
        d["RunTimeTicks"] = it["ms"] * TICKS
        d["MediaSources"] = [dict(Path=it["path"], Protocol="File", RunTimeTicks=it["ms"] * TICKS)]
    if it["type"] == "Episode":
        d.update(SeriesName=it["series"], SeriesId=it["seriesId"], SeasonId=it["seasonId"], ParentIndexNumber=it["season"], IndexNumber=it["episode"],
                 SeriesPrimaryImageTag="stag", ParentPrimaryImageItemId=it["seasonId"], ParentPrimaryImageTag="ptag")
    name = it["series"] or it["name"]
    if it["type"] in ("Movie", "Series") and name in RATINGS:
        d["CommunityRating"] = RATINGS[name]
        d["CriticRating"] = 97   # Rotten Tomatoes': never used
    if user:
        s = st(user, iid)
        d["UserData"] = dict(PlaybackPositionTicks=s["pos"] * TICKS, Played=s["played"], PlayCount=s["count"], LastPlayedDate=iso(s["last"]))
    return d


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

    def parts(self):
        u = urlparse(self.path)
        return u.path, {k.lower(): v[0] for k, v in parse_qs(u.query, keep_blank_values=True).items()}

    def body(self):
        n = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(n) if n else b""
        try:
            return json.loads(raw or b"{}")
        except Exception:
            return {}

    def who(self):
        """(user id or "apikey", admin) for the request's token, else (None, False)."""
        tok = self.headers.get("X-Emby-Token") or ""
        m = re.search(r'Token="([^"]*)"', self.headers.get("Authorization") or "")
        if m:
            tok = tok or m.group(1)
        if tok == API_KEY:
            return "apikey", True
        for uid, (_, _, admin, t) in USERS.items():
            if tok == t:
                return uid, admin
        return None, False

    def auth_result(self, uid):
        name, _, admin, tok = USERS[uid]
        return dict(AccessToken=tok, ServerId=SERVER_ID, User=dict(Id=uid, Name=name, Policy=dict(IsAdministrator=admin, IsDisabled=False)))

    def png(self):
        data = PNG["data"]
        self.send_response(200)
        self.send_header("Content-Type", "image/png")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path, q = self.parts()
        if path.startswith("/_test/"):
            return self.test(path, q)
        if path == "/System/Info/Public":
            return self.send(dict(Id=SERVER_ID, ServerName=SERVER_NAME, Version="10.11.2", LocalAddress=f"http://{IP}:{PORT}"))
        if path == "/QuickConnect/Enabled":
            return self.send(True)
        if path == "/QuickConnect/Connect":
            if q.get("secret") != "qsecret":
                return self.send(dict(error="unknown secret"), 404)
            return self.send(dict(Secret="qsecret", Code="246810", Authenticated=QUICK["approved"]))
        if re.match(r"^/Items/[0-9a-f]+/Images/Primary$", path):
            return self.png()
        uid, admin = self.who()
        if uid is None:
            return self.send(dict(error="unauthorized"), 401)
        user = q.get("userid") or ("" if uid == "apikey" else uid)
        if user and user != uid and not admin:
            return self.send(dict(error="forbidden"), 403)
        with lock:
            if path == "/Users":
                if not admin:
                    return self.send(dict(error="admin only"), 403)
                return self.send([dict(Id=k, Name=v[0], Policy=dict(IsAdministrator=v[2], IsDisabled=False)) for k, v in USERS.items()])
            if path == "/Library/VirtualFolders":
                return self.send([dict(Name="Movies", CollectionType="movies", Locations=[CURRENT["root"] + "/Movies"]),
                                  dict(Name="Shows", CollectionType="tvshows", Locations=[CURRENT["root"] + "/TV"])])
            if path == "/Sessions":
                if not admin:
                    return self.send(dict(error="admin only"), 403)
                out = [dict(Id="sess-idle", UserId="u-wife", UserName="Wife", Client="Jellyfin Web")]   # signed in, playing nothing
                for u, s in SESSIONS.items():
                    item = dto(s["id"], u)
                    out.append(dict(Id="sess-" + u, UserId=u, UserName=USERS[u][0], Client="Jellyfin Android TV", DeviceId="dev-secret",
                                    NowPlayingItem=item, PlayState=dict(PositionTicks=s["pos"] * TICKS, IsPaused=s["paused"])))
                return self.send(out)
            if path == "/Items":
                types = q.get("includeitemtypes", "").split(",")
                ids = [k for k, it in sorted(ITEMS.items()) if it["type"] in types]
                items = [dto(k, user) for k in ids]
                if "Series" in types:   # the ratings listing: one movie only Jellyfin has
                    items.append(dict(Id=gid(0xABCD), Name=ONLY_HERE["name"], Type="Movie", ProductionYear=ONLY_HERE["year"], Path=ONLY_HERE["path"],
                                      CommunityRating=ONLY_HERE["rating"], ImageTags=dict(Primary="x")))
                start, limit = int(q.get("startindex", 0)), int(q.get("limit", 100000))
                return self.send(dict(Items=items[start:start + limit], TotalRecordCount=len(items)))
            m = re.match(r"^/(?:Users/([^/]+)/)?Items/([0-9a-f]+)$", path)
            if m:
                iid = m.group(2)
                if iid not in ITEMS:
                    return self.send(dict(error="not found"), 404)
                return self.send(dto(iid, m.group(1) or user))
            m = re.match(r"^/Shows/([0-9a-f]+)/Episodes$", path)
            if m:
                return self.send(dict(Items=[dto(k, user) for k, it in sorted(ITEMS.items()) if it["seriesId"] == m.group(1)]))
        return self.send(dict(error="unknown " + path), 404)

    def do_POST(self):
        path, q = self.parts()
        body = self.body()
        if path == "/Users/AuthenticateByName":
            for uid, (name, pw, _, _) in USERS.items():
                if body.get("Username", "").lower() == name.lower() and body.get("Pw") == pw:
                    return self.send(self.auth_result(uid))
            return self.send(dict(error="bad"), 401)
        if path == "/QuickConnect/Initiate":
            QUICK["approved"] = False
            return self.send(dict(Secret="qsecret", Code="246810", Authenticated=False))
        if path == "/Users/AuthenticateWithQuickConnect":
            if body.get("Secret") == "qsecret" and QUICK["approved"]:
                return self.send(self.auth_result("u-chet"))
            return self.send(dict(error="not approved"), 401)
        return self.write(path, q, body, played=True)

    def do_DELETE(self):
        path, q = self.parts()
        return self.write(path, q, {}, played=False)

    def write(self, path, q, body, played):
        uid, admin = self.who()
        if uid is None:
            return self.send(dict(error="unauthorized"), 401)
        m = re.match(r"^/(?:Users/([^/]+)/)?(UserItems|Items|UserPlayedItems|PlayedItems)/([0-9a-f]+)(/UserData)?$", path)
        if not m:
            return self.send(dict(error="unknown " + path), 404)
        user = m.group(1) or q.get("userid") or uid
        if user != uid and not admin:
            return self.send(dict(error="forbidden"), 403)
        iid = m.group(3)
        with lock:
            if iid not in ITEMS:
                return self.send(dict(error="not found"), 404)
            targets = [iid] if ITEMS[iid]["type"] != "Series" else [k for k, it in ITEMS.items() if it["seriesId"] == iid]
            now = int(time.time())
            if m.group(4):   # UserData: the resume point
                WRITES.append(dict(user=user, what="userdata", id=iid, pos=body.get("PlaybackPositionTicks", 0) // TICKS))
                s = st(user, iid)
                s["pos"] = int(body.get("PlaybackPositionTicks", 0)) // TICKS
                if "Played" in body:
                    s["played"] = bool(body["Played"])
                s["last"] = now
                return self.send(dto(iid, user))
            WRITES.append(dict(user=user, what="played" if played else "unplayed", id=iid))
            for k in targets:
                s = st(user, k)
                if played:
                    s.update(played=True, pos=0, count=s["count"] + 1, last=now)
                else:
                    s.update(played=False, pos=0, count=0)
            return self.send(dict(Played=played))

    def test(self, path, q):
        with lock:
            if path == "/_test/play":
                SESSIONS[q["user"]] = dict(id=q["id"], pos=int(q["pos"]), paused=q.get("paused") == "1")
                return self.send({"ok": True})
            if path == "/_test/stop":
                s = SESSIONS.pop(q["user"], None)
                if s:
                    pos = int(q.get("final", s["pos"]))
                    rec = st(q["user"], s["id"])
                    if pos >= ITEMS[s["id"]]["ms"] * 0.9:
                        rec.update(played=True, pos=0, count=rec["count"] + 1, last=int(time.time()))
                    else:
                        rec.update(pos=pos, last=int(time.time()))
                return self.send({"ok": True})
            if path == "/_test/item":
                iid = q["id"]
                return self.send(dict(st(q["user"], iid), writes=[w for w in WRITES if w["user"] == q["user"] and w["id"] in (iid, ITEMS[iid]["seriesId"])]))
            if path == "/_test/ids":
                return self.send({it["path"].rsplit("/", 1)[1]: k for k, it in ITEMS.items() if it["type"] != "Series"})
            if path == "/_test/set":
                st(q["user"], q["id"]).update(pos=int(q.get("pos", 0)), played=q.get("played") == "1", count=int(q.get("played", "0")), last=int(time.time()))
                return self.send({"ok": True})
            if path == "/_test/extra":
                # The same as mock_plex's: Dune (2021) on both servers only, and another show's S01E01.
                r = CURRENT["root"]
                ITEMS[gid(0x5001)] = dict(type="Movie", name="Dune", series="", seriesId="", seasonId="", season=0, episode=0, year=2021,
                                          path=r + "/Movies/Dune (2021)/Dune (2021).mkv", ms=9300000)
                ITEMS[gid(0x5002)] = dict(type="Movie", name="Other Show", series="", seriesId="", seasonId="", season=0, episode=0, year=2020,
                                          path=r + "/TV/Other Show/Season 01/S01E01.mkv", ms=2400000)
                return self.send({"ok": True})
            if path == "/_test/root":
                # Jellyfin seeing the share somewhere else (/data/media): for automatic matching with no folder mapping.
                new = q.get("to", ROOT)
                for it in list(ITEMS.values()) + [ONLY_HERE]:
                    if it["path"].startswith(CURRENT["root"]):
                        it["path"] = new + it["path"][len(CURRENT["root"]):]
                CURRENT["root"] = new
                return self.send({"ok": True})
            if path == "/_test/approve":
                QUICK["approved"] = True
                return self.send({"ok": True})
        return self.send({"error": "unknown"}, 404)


def discovery():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.bind((IP, 7359))
    while True:
        data, addr = s.recvfrom(1024)
        if b"jellyfin" in data.lower():
            s.sendto(json.dumps(dict(Address=f"http://{IP}:{PORT}", Id=SERVER_ID, Name=SERVER_NAME)).encode(), addr)


def start(files, png):
    PNG["data"] = png
    setup(files)
    srv = ThreadingHTTPServer((IP, PORT), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    threading.Thread(target=discovery, daemon=True).start()
