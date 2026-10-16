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

# ---------------------------------------------------------------- 13. exact stop time: the player's own saved bookmark wins
mock(LR, "play", url=ARRIVAL, pos=1000000)
time.sleep(4)
mock(LR, "stop", final=1002600, delay=1200)    # player saves 2.6 s past the last poll, a moment after stopping
check("copies the exact stop time the player saved, not the last polled one",
      wait_for(lambda: bookmark(BR, ARRIVAL)["playPoint"] == 1002600), str(bookmark(BR, ARRIVAL)["playPoint"]))


def events():
    return http("GET", RELAY + "/api/events?n=50")


def card_for(title, pos):
    return next((e for e in events() if e["title"] == title and e["positionMs"] == pos), None)


# ---------------------------------------------------------------- 14. Activity cards: targets with states, and a poster
card = wait_for(lambda: (lambda c: c if c and all(t["state"] != "Pending" for t in c["targets"]) else None)(card_for("Arrival", 1002600)), 20)
check("stop card exists for Arrival with the source", card and card["fromName"] == "Living Room", str(card))
states = {t["name"]: (t["state"], t.get("note", "")) for t in (card or {}).get("targets", [])}
check("Bedroom chip is ok", states.get("Bedroom", ("",))[0] == "Ok", str(states))
check("Home Theater Room chip is skipped (not in library)", states.get("Home Theater Room", ("",))[0] == "Skipped", str(states))
poster_ok = False
if card and card.get("poster"):
    with urllib.request.urlopen(RELAY + card["poster"], timeout=10) as r:
        poster_ok = r.read(8) == b"\x89PNG\r\n\x1a\n"
check("card poster is served as an image", poster_ok, str(card and card.get("poster")))

# ---------------------------------------------------------------- 15. episode card: SxxEyy and the season poster
play_then_stop(LR, E04, 1200000)
ep = wait_for(lambda: card_for("Andor", 1200000), 20)
check("episode card subtitle is S01E04", ep and ep["subtitle"] == "S01E04", str(ep))
st = bookmark(LR, E04)
season_ids = [int(k) for k, v in st["titles"].items() if v["type"] == 4]
check("season poster was requested", any(i in st["posterRequests"] for i in season_ids), f"{st['posterRequests']} vs {season_ids}")
check("finished-looking films say so: Arrival card is not finished", card and not card["finished"])


def dev(ip):
    return next(d for d in http("GET", RELAY + "/api/state")["devices"] if d["ip"] == ip)


# ---------------------------------------------------------------- 16. power off, waiting copies on the card, power on by Wake-on-LAN
check("Home Theater Room reports power on and Home Theater running",
      wait_for(lambda: dev(HT)["power"] == "on" and dev(HT)["homeTheater"] is True, 30), str(dev(HT)))
check("player can be woken (MAC known)", dev(HT)["canWake"])
http("POST", RELAY + "/api/devices/" + urllib.parse.quote(ids[HT]) + "/power", {"on": False})
check("power off shows turning off", dev(HT)["power"] in ("turningOff", "off"), dev(HT)["power"])
check("power off reaches off", wait_for(lambda: dev(HT)["power"] == "off", 40), dev(HT)["power"])
check("power off sent the power key", "poweroff" in bookmark(HT, BRAVE)["powerLog"])
play_then_stop(LR, E03, 1700000)
check("waiting copy is listed on the off player's card",
      wait_for(lambda: any(w["positionMs"] == 1700000 for w in dev(HT)["waiting"]), 20), str(dev(HT)["waiting"]))
ev = wait_for(lambda: (lambda c: c if c and any(t["name"] == "Home Theater Room" and t["state"] == "Waiting" for t in c["targets"]) else None)(card_for("Andor", 1700000)), 20)
check("its chip is waiting", ev, str(card_for("Andor", 1700000)))
http("POST", RELAY + "/api/devices/" + urllib.parse.quote(ids[HT]) + "/power", {"on": True})
check("power on shows turning on", dev(HT)["power"] in ("turningOn", "on"), dev(HT)["power"])
check("Wake-on-LAN brings it up, Home Theater opened",
      wait_for(lambda: dev(HT)["power"] == "on" and dev(HT)["homeTheater"] is True, 60), str(dev(HT)))
log = bookmark(HT, BRAVE)["powerLog"]
check("woken by the magic packet, then openApp", "wol" in log and "openApp" in log[log.index("wol"):], str(log))
check("waiting copy delivered after waking", wait_for(lambda: bookmark(HT, E03)["playPoint"] == 1700000, 40))
ev = wait_for(lambda: (lambda c: c if c and any(t["name"] == "Home Theater Room" and t["state"] == "Ok" for t in c["targets"]) else None)(card_for("Andor", 1700000)), 20)
check("waiting chip turned ok", ev, str(card_for("Andor", 1700000)))
check("waiting list on the card emptied", wait_for(lambda: not dev(HT)["waiting"], 10), str(dev(HT)["waiting"]))

# ---------------------------------------------------------------- 17. Launch HT
mock(BR, "htclosed", on=1)
check("Home Theater closed is reported", wait_for(lambda: dev(BR)["homeTheater"] is False, 40), str(dev(BR)["homeTheater"]))
http("POST", RELAY + "/api/devices/" + urllib.parse.quote(ids[BR]) + "/launch")
check("Launch HT opens it", wait_for(lambda: dev(BR)["homeTheater"] is True, 20), str(dev(BR)["homeTheater"]))

# ---------------------------------------------------------------- 18. now playing carries poster and subtitle
mock(LR, "play", url=E03, pos=300000)
np_ = wait_for(lambda: (dev(LR)["playing"] or {}).get("poster") and dev(LR)["playing"], 15)
check("now playing shows S01E03 with a poster", np_ and np_["subtitle"] == "S01E03" and np_["poster"], str(np_))
mock(LR, "stop")

# ---------------------------------------------------------------- 19. a stop at 0:00 resets the title on the others
mock(BR, "setlwt", url=BRAVE, lwt=int(time.time() * 1000), pp=3000000)
mock(HT, "setlwt", url=BRAVE, lwt=int(time.time() * 1000), pp=3000000)
mock(LR, "play", url=BRAVE, pos=0)
time.sleep(4)
mock(LR, "stop", final=0)
check("stop at 0:00 resets Bedroom", wait_for(lambda: bookmark(BR, BRAVE)["playPoint"] == 0 and bookmark(BR, BRAVE)["lastWatchTime"] == -1), str(bookmark(BR, BRAVE)))
check("...and Home Theater Room", wait_for(lambda: bookmark(HT, BRAVE)["playPoint"] == 0), str(bookmark(HT, BRAVE)["playPoint"]))
w = bookmark(BR, BRAVE)["writes"][-1]
check("reset is sent as playPoint=0 on the file's entry", w.get("playPoint") == "0" and int(w["id"]) == 18000, str(w))
rc = wait_for(lambda: (lambda c: c if c and c.get("reset") and all(t["state"] == "Ok" and t["note"] == "reset" for t in c["targets"]) else None)(card_for("Braveheart", 0)), 20)
check("card says reset, chips say reset", rc, str(card_for("Braveheart", 0)))
check("logged as a reset", log_has("Reset Braveheart on Bedroom (stopped at 0:00 on Living Room)."))

passed = sum(1 for _, ok in results if ok)
print(f"\n{passed}/{len(results)} passed")
sys.exit(0 if passed == len(results) else 1)
