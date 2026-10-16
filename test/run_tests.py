"""End-to-end scenarios for Resume Relay against mock_zidoo.py. Run with the relay on :8090."""
import json, time, urllib.request, urllib.parse, sys

RELAY = "http://127.0.0.1:8090"
LR, BR, HT = "127.0.0.2", "127.0.0.3", "127.0.0.4"
SHARE = "smb://10.0.0.56/Entertainment"
BRAVE = SHARE + "/Movies/Braveheart/Braveheart (1995).4K.(1#Theatrical Movie).mkv"
E03 = SHARE + "/TV/Andor/Season 01/Andor - S01E03.mkv"
E04 = SHARE + "/TV/Andor/Season 01/Andor - S01E04.mkv"
ARRIVAL = SHARE + "/Movies/Arrival (2016)/Arrival (2016).mkv"
results = []


def http(method, url, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/json"} if data else {})
    with urllib.request.urlopen(req, timeout=20) as r:
        t = r.read().decode()
        return json.loads(t) if t else None


def mock(ip, path, **q):
    return http("GET", f"http://{ip}:9529/_test/{path}?" + urllib.parse.urlencode(q))


def bookmark(ip, url):
    return mock(ip, "state", url=url)


def activity(n=60):
    return [e["message"] for e in http("GET", RELAY + "/api/activity?n=%d" % n)]


def wait_for(pred, timeout=25, what=""):
    end = time.time() + timeout
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(0.5)
    return None


def check(name, ok, detail=""):
    results.append((name, bool(ok)))
    print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail and not ok else ""), flush=True)


def log_has(fragment, timeout=25):
    return wait_for(lambda: any(fragment in m for m in activity(80)), timeout)


def settings(**kw):
    s = http("GET", RELAY + "/api/state")["settings"]
    s.update(kw)
    http("POST", RELAY + "/api/settings", s)


def play_then_stop(ip, url, pos):
    mock(ip, "play", url=url, pos=pos)
    time.sleep(4)                 # > one poll interval, so the relay sees it playing
    mock(ip, "stop")


# ---------------------------------------------------------------- setup
state = http("GET", RELAY + "/api/state")
ids = {d["ip"]: d["id"] for d in state["devices"]}
for ip, name in ((LR, "Living Room"), (BR, "Bedroom"), (HT, "Home Theater Room")):
    http("PATCH", RELAY + "/api/devices/" + urllib.parse.quote(ids[ip]), {"name": name})
names = {d["ip"]: d["name"] for d in http("GET", RELAY + "/api/state")["devices"]}
check("rename players", names == {LR: "Living Room", BR: "Bedroom", HT: "Home Theater Room"}, str(names))
wait_for(lambda: all(d["online"] for d in http("GET", RELAY + "/api/state")["devices"]), 15)
check("all players online", all(d["online"] for d in http("GET", RELAY + "/api/state")["devices"]))

# ---------------------------------------------------------------- 1. watch-only writes nothing
settings(watchOnly=True)
play_then_stop(LR, BRAVE, 4059639)
check("watch-only logs the would-be copy", log_has("[watch-only] Would copy Braveheart (1:07:39) from Living Room to Bedroom"))
check("watch-only wrote nothing", bookmark(BR, BRAVE)["playPoint"] == 0 and not bookmark(BR, BRAVE)["writes"])

# ---------------------------------------------------------------- 2. the real thing
settings(watchOnly=False)
play_then_stop(LR, BRAVE, 4059639)
ok_br = wait_for(lambda: bookmark(BR, BRAVE)["playPoint"] == 4059639)
ok_ht = wait_for(lambda: bookmark(HT, BRAVE)["playPoint"] == 4059639)
check("Braveheart copied Living Room -> Bedroom (player that reports false errors)", ok_br)
check("Braveheart copied Living Room -> Home Theater Room", ok_ht)
check("logged as Copied for Bedroom despite the error reply", log_has("Copied Braveheart (1:07:39) from Living Room to Bedroom"))
lw_src = bookmark(LR, BRAVE)["lastWatchTime"]
lw_br = bookmark(BR, BRAVE)["lastWatchTime"]
check("copy carries a last-watched time close to the stop (not 'now' at write time)", abs(lw_br - lw_src) < 5000, f"{lw_br} vs {lw_src}")
w = bookmark(BR, BRAVE)["writes"][-1]
check("write used the entry id (fileId), not videoInfoId", int(w["id"]) == 18000, str(w))

# ---------------------------------------------------------------- 3. resume in Bedroom, stop later -> flows back
play_then_stop(BR, BRAVE, 5000000)
check("later stop in Bedroom flows back to Living Room", wait_for(lambda: bookmark(LR, BRAVE)["playPoint"] == 5000000))
check("...and to Home Theater Room", wait_for(lambda: bookmark(HT, BRAVE)["playPoint"] == 5000000))

# ---------------------------------------------------------------- 4. latest stop always wins + missing file
future = int(time.time() * 1000) + 3600_000
mock(BR, "setlwt", url=ARRIVAL, lwt=future, pp=1234000)       # Bedroom holds a "newer" Arrival bookmark
play_then_stop(LR, ARRIVAL, 2000000)
check("latest stop overwrites even a newer bookmark (0.1.3 rule)", wait_for(lambda: bookmark(BR, ARRIVAL)["playPoint"] == 2000000))
check("Home Theater Room lacks Arrival -> skipped, not an error", log_has("Home Theater Room's library does not have that file"))

# ---------------------------------------------------------------- 5. sleeping player gets it on wake
mock(HT, "offline", on=1)
wait_for(lambda: not next(d for d in http("GET", RELAY + "/api/state")["devices"] if d["ip"] == HT)["online"], 20)
play_then_stop(BR, E03, 1500000)
check("copy reaches awake Living Room", wait_for(lambda: bookmark(LR, E03)["playPoint"] == 1500000))
time.sleep(3)
pend = http("GET", RELAY + "/api/state")["pending"]
check("copy for sleeping Home Theater Room is held in the queue", [x["to"] for x in pend] == ["Home Theater Room"], str(pend))
mock(HT, "offline", on=0)
check("delivered when Home Theater Room wakes", wait_for(lambda: bookmark(HT, E03)["playPoint"] == 1500000, 30))
check("queue empty afterwards", wait_for(lambda: not http("GET", RELAY + "/api/state")["pending"], 10))

# ---------------------------------------------------------------- 6. next episode auto-play counts as a stop
mock(LR, "play", url=E03, pos=2350000)          # 2 s... near the end of a 40-min episode
time.sleep(4)
mock(LR, "play", url=E04, pos=5000)              # auto-play switches file
check("switching files copies the finished episode (marked watched)", log_has("Andor S01E03 (39:10) from Living Room to Bedroom (marked watched)"))
mock(LR, "stop")

# ---------------------------------------------------------------- 7. short stops are copied too (no minimum in 0.1.3)
play_then_stop(BR, ARRIVAL, 20000)
check("a 20-second stop is copied", wait_for(lambda: bookmark(LR, ARRIVAL)["playPoint"] == 20000))

# ---------------------------------------------------------------- 8. player switched off mid-film
mock(HT, "play", url=BRAVE, pos=6000000)
time.sleep(4)
mock(HT, "offline", on=1)
check("player going offline mid-film copies its last position", wait_for(lambda: bookmark(LR, BRAVE)["playPoint"] == 6000000, 40))
mock(HT, "offline", on=0)
mock(HT, "stop")

# ---------------------------------------------------------------- 9. don't write to a player playing that file
mock(BR, "play", url=E04, pos=600000)
time.sleep(4)
play_then_stop(LR, E04, 900000)
check("skips a player that is playing that file right now", log_has("Not copying Andor S01E04 (15:00) from Living Room to Bedroom: it is playing there right now."))
mock(BR, "stop")

# ---------------------------------------------------------------- 10. another player took this IP
mock(HT, "setmac", mac="aa:bb:cc:dd:ee:ff")
mock(HT, "offline", on=1); time.sleep(8); mock(HT, "offline", on=0)
check("detects a different player at a known IP", log_has("is no longer Home Theater Room", 30))
mock(HT, "setmac", mac="80:0a:80:5c:da:26")
wait_for(lambda: next(d for d in http("GET", RELAY + "/api/state")["devices"] if d["ip"] == HT)["online"], 30)

# ---------------------------------------------------------------- 11. player on, Home Theater app closed
mock(BR, "htclosed", on=1)
play_then_stop(LR, E03, 1000000)
check("copy waits while Bedroom's Home Theater app is closed", log_has("Bedroom is on, but its Home Theater app isn't open"))
time.sleep(60)  # several queue passes: in 0.1.2 each of these counted toward giving up
msgs = activity(40)
check("waiting never counts as a failed attempt", not any("Bedroom" in m and ("Will retry" in m or "Gave up" in m) for m in msgs))
check("still queued after a minute", any(x["to"] == "Bedroom" for x in http("GET", RELAY + "/api/state")["pending"]))
mock(BR, "htclosed", on=0)
check("delivered once the Home Theater app opens", wait_for(lambda: bookmark(BR, E03)["playPoint"] == 1000000, 30))

# ---------------------------------------------------------------- 12. a waiting copy is discarded when that player watches the film itself
mock(HT, "htclosed", on=1)
play_then_stop(LR, BRAVE, 7000000)
wait_for(lambda: any(x["to"] == "Home Theater Room" for x in http("GET", RELAY + "/api/state")["pending"]), 15)
mock(HT, "play", url=BRAVE, pos=8000000)
time.sleep(4)
mock(HT, "stop")
check("older waiting copy discarded when that player watches the film", log_has("Discarded an older waiting copy of Braveheart for Home Theater Room"))
mock(HT, "htclosed", on=0)
time.sleep(20)
check("...so the player keeps its own newer position", bookmark(HT, BRAVE)["playPoint"] == 8000000, str(bookmark(HT, BRAVE)["playPoint"]))
check("...and that position reaches the others", wait_for(lambda: bookmark(LR, BRAVE)["playPoint"] == 8000000))

passed = sum(1 for _, ok in results if ok)
print(f"\n{passed}/{len(results)} passed")
sys.exit(0 if passed == len(results) else 1)
