"""End-to-end scenarios for Resume Relay against mock_zidoo.py. Run with the relay on :8090."""
import json, time, urllib.request, urllib.parse, urllib.error, sys

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
check("now playing names the file", np_ and np_.get("file") == "Andor - S01E03.mkv", str(np_ and np_.get("file")))
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

# ---------------------------------------------------------------- 20. file name on the card, HT version on the player
fc = card_for("Braveheart", 0)
check("stop card names the exact file", fc and fc.get("fileName") == "Braveheart (1995).4K.(1#Theatrical Movie).mkv", str(fc and fc.get("fileName")))
check("player reports its Home Theater version", wait_for(lambda: dev(LR).get("htVersion") == "5.1.11" and dev(LR).get("htVersionCode") == 5111, 40), str((dev(LR).get("htVersion"), dev(LR).get("htVersionCode"))))

# ---------------------------------------------------------------- 21. "Reset Progress" on an Activity card
mock(LR, "setlwt", url=E04, lwt=int(time.time() * 1000), pp=1300000)
mock(HT, "setlwt", url=E04, lwt=int(time.time() * 1000), pp=1300000)
play_then_stop(BR, E04, 1400000)
src = wait_for(lambda: (lambda c: c if c and all(t["state"] == "Ok" for t in c["targets"]) else None)(card_for("Andor", 1400000)), 25)
check("normal stop card offers Reset Progress", src and src.get("canReset") and not src.get("reset"), str(src))
http("POST", RELAY + f"/api/events/{src['id']}/reset")
check("reset clears the source player too", wait_for(lambda: bookmark(BR, E04)["playPoint"] == 0, 25), str(bookmark(BR, E04)["playPoint"]))
check("...and every other player", wait_for(lambda: bookmark(LR, E04)["playPoint"] == 0 and bookmark(HT, E04)["playPoint"] == 0, 25))
mc = wait_for(lambda: next((e for e in events() if e.get("manual") and e["fileName"] == "Andor - S01E04.mkv"
                            and all(t["state"] == "Ok" and t["note"] == "reset" for t in e["targets"])), None), 25)
check("new card from Resume Relay lists all three players as reset", mc and mc["fromName"] == "Resume Relay" and len(mc["targets"]) == 3, str(mc))
check("logged per player", log_has("Reset Andor S01E04 on Bedroom (reset from Resume Relay)."))

# ---------------------------------------------------------------- 22. Progress Reset tab: read every player
FEAT = SHARE + "/Movies/Braveheart/Featurettes/Braveheart (1995).(7#A Writer's Journal).mkv"
now = int(time.time() * 1000)
for ip, url, pp in [(LR, BRAVE, 3000000), (BR, BRAVE, 0), (HT, BRAVE, 0),
                    (LR, ARRIVAL, 0), (BR, ARRIVAL, 1500000),
                    (LR, E03, 600000), (BR, E03, 600000), (HT, E03, 0),
                    (LR, E04, 0), (BR, E04, 0), (HT, E04, 900000),
                    (LR, FEAT, 0), (BR, FEAT, 0)]:
    mock(ip, "setlwt", url=url, lwt=now if pp else -1, pp=pp)
http("POST", RELAY + "/api/progress/read", {"deviceIds": list(ids.values())})
wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 60)
prog = http("GET", RELAY + "/api/progress")
check("all three players read", len([d for d in prog["devices"] if d["readUtc"]]) == 3, str(prog["devices"]))
files = {f["key"]: f for f in prog["files"]}
kb = "/movies/braveheart/braveheart (1995).4k.(1#theatrical movie).mkv"
ka = "/movies/arrival (2016)/arrival (2016).mkv"
k3, k4 = "/tv/andor/season 01/andor - s01e03.mkv", "/tv/andor/season 01/andor - s01e04.mkv"
check("movie listed by its full file name", files.get(kb, {}).get("display") == "Braveheart (1995).4K.(1#Theatrical Movie).mkv", str(files.get(kb)))
check("movie inside a collection is found", ka in files and files[ka]["cells"][ids[BR]]["playPointMs"] == 1500000, str(files.get(ka)))
check("player without the file has no cell (not in library)", ka in files and ids[HT] not in files[ka]["cells"])
check("player with the file but no progress shows 0 (not started)", files.get(kb, {}).get("cells", {}).get(ids[BR], {}).get("playPointMs") == 0)
check("episode shown as Show - SxxEyy", files.get(k3, {}).get("display") == "Andor - S01E03", str(files.get(k3, {}).get("display")))
check("episode cell has position, length and date", (lambda c: c["playPointMs"] == 600000 and c["durationMs"] == 2400000 and c["lastWatchMs"] > 0)(files[k3]["cells"][ids[LR]]) if k3 in files else False)
check("files with no progress anywhere are left out", not any("writer's journal" in k for k in files), str(list(files)))

# ---------------------------------------------------------------- 23. Reset one row
http("POST", RELAY + "/api/progress/reset", {"key": ka})
check("row reset clears the file on every player", wait_for(lambda: bookmark(BR, ARRIVAL)["playPoint"] == 0, 25))
rc = wait_for(lambda: next((e for e in events() if e.get("manual") and e["fileName"] == "Arrival (2016).mkv" and not e.get("series")
                            and all(t["state"] != "Pending" for t in e["targets"])), None), 25)
check("row reset adds a Resume Relay card", rc and any(t["state"] == "Ok" for t in rc["targets"]), str(rc))
check("reset row leaves the list without reading again", wait_for(lambda: ka not in {f["key"] for f in http("GET", RELAY + "/api/progress")["files"]}, 20))

# ---------------------------------------------------------------- 24. Reset TV Show
mock(LR, "setlwt", url=E04, lwt=now, pp=700000, watched=1)
http("POST", RELAY + "/api/progress/reset-series", {"key": k3})
check("TV show reset clears every episode on every player",
      wait_for(lambda: all(bookmark(ip, u)["playPoint"] == 0 for ip in (LR, BR, HT) for u in (E03, E04)), 30),
      str([(ip, u[-10:], bookmark(ip, u)["playPoint"]) for ip in (LR, BR, HT) for u in (E03, E04)]))
sc = wait_for(lambda: next((e for e in events() if e.get("series") and e["title"] == "Andor"
                            and all(t["state"] == "Ok" for t in e["targets"])), None), 30)
check("TV show reset card: one chip per player with episode counts", sc and len(sc["targets"]) == 3
      and any(t["note"].startswith("2 episodes reset") for t in sc["targets"]), str(sc))
check("the show's rows leave the list", wait_for(lambda: not ({k3, k4} & {f["key"] for f in http("GET", RELAY + "/api/progress")["files"]}), 20))

# ---------------------------------------------------------------- 25. Watch Sync tab
now = int(time.time() * 1000)
for ip, url, pp, w in [(LR, BRAVE, 0, 1), (BR, BRAVE, 0, 0), (HT, BRAVE, 0, 0),
                       (LR, ARRIVAL, 0, 1), (BR, ARRIVAL, 0, 1),
                       (LR, E03, 0, 0), (BR, E03, 0, 0), (HT, E03, 0, 0)]:
    mock(ip, "setlwt", url=url, lwt=now if w else -1, pp=pp, watched=w)
http("POST", RELAY + "/api/progress/read", {"deviceIds": list(ids.values())})
wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 60)
allp = ",".join(ids.values())
def wl(show):
    return {f["key"]: f for f in http("GET", RELAY + f"/api/watched?show={show}&players={urllib.parse.quote(allp)}")["files"]}
diff = wl("different")
check("watched on one player only shows as different", kb in diff and diff[kb]["cells"][ids[LR]]["watched"] and not diff[kb]["cells"][ids[BR]]["watched"], str(diff.get(kb)))
check("watched everywhere it exists is not different", ka not in diff, str(list(diff)))
check("watched list includes it", ka in wl("watched"))
check("never watched anywhere is in unwatched", k3 in wl("unwatched"))
r = http("POST", RELAY + "/api/watched/set", {"key": kb, "deviceIds": [ids[BR], ids[HT]], "watched": True})
check("mark watched on two players reports success", r and all(x["ok"] for x in r), str(r))
check("...and the players show it watched", bookmark(BR, BRAVE)["watched"] and bookmark(HT, BRAVE)["watched"])
check("...using markAsWatched", any(w.get("call") == "markAsWatched" for w in bookmark(BR, BRAVE)["writes"]))
check("no longer different once all agree", kb not in wl("different"))
mock(LR, "setlwt", url=E03, lwt=now, pp=1000000, watched=1)
http("POST", RELAY + "/api/progress/read", {"deviceIds": [ids[LR]]})
wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 60)
r = http("POST", RELAY + "/api/watched/set", {"key": k3, "deviceIds": [ids[LR]], "watched": False})
st = bookmark(LR, E03)
check("mark unwatched keeps the resume point", r and r[0]["ok"] and not st["watched"] and st["playPoint"] == 1000000, f"{r} {st['watched']} {st['playPoint']}")
check("mark unwatched never sends watched=0", not any(w.get("call") == "markAsWatched" and w.get("watched") == "0" for w in st["writes"]))

# ---------------------------------------------------------------- 26. Watch Sync: missing filter, pages, sort
miss = wl("missing")
check("missing filter lists files a player lacks", ka in miss and kb not in miss, str(list(miss)))
def page(n, sort="title"):
    return http("GET", RELAY + f"/api/watched?show=all&players={urllib.parse.quote(allp)}&sort={sort}&page={n}&pageSize=2")
p1, p2 = page(1), page(2)
check("pages: page 1 and 2 hold different files", p1["pages"] >= 2 and not ({f["key"] for f in p1["files"]} & {f["key"] for f in p2["files"]}), str((p1["pages"], p1["total"])))
allkeys = [f["key"] for n in range(1, p1["pages"] + 1) for f in page(n)["files"]]
check("pages: together they cover every file once", len(allkeys) == p1["total"] == len(set(allkeys)), str((len(allkeys), p1["total"])))
check("sort by title runs across pages", [f["display"].lower() for f in page(1)["files"]] <= [f["display"].lower() for f in page(2)["files"]])

# ---------------------------------------------------------------- 27. diagnostics page
with urllib.request.urlopen(RELAY + "/api/progress/diagnostics?q=sci-fi", timeout=20) as r:
    diag = r.read().decode()
check("diagnostics names how each collection was opened", "Collection \"Sci-Fi Collection\"" in diag and "via getCollection?id=" in diag and "Arrival" in diag, diag[:400])

# ---------------------------------------------------------------- 28. a player that doesn't answer every request
def br_files():
    st = http("GET", RELAY + "/api/progress")
    return next(d for d in st["devices"] if d["deviceId"] == ids[BR]), sum(1 for f in st["files"] if ids[BR] in f["cells"])
def read_all():
    http("POST", RELAY + "/api/progress/read", {"deviceIds": list(ids.values())})
    time.sleep(1)
    wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 120)
def br_cells():
    w = http("GET", RELAY + f"/api/watched?show=all&players={urllib.parse.quote(ids[BR])}&pageSize=2000")
    return {f["key"] for f in w["files"] if ids[BR] in f["cells"]}
read_all()
before = br_cells()
mock(BR, "flaky", every=2)          # every other title request fails
read_all()
dev_br = next(d for d in http("GET", RELAY + "/api/progress")["devices"] if d["deviceId"] == ids[BR])
check("busy player: every title is still read (asked again)", br_cells() == before and dev_br["unread"] == 0, f"{len(br_cells())} vs {len(before)}, unread {dev_br['unread']}")
arrival_title = next(int(k) for k, v in bookmark(BR, ARRIVAL)["titles"].items() if v["name"] == "Arrival" and v["type"] == 1)
mock(BR, "flaky", every=0, fail=arrival_title)   # one title never answers
read_all()
dev_br = next(d for d in http("GET", RELAY + "/api/progress")["devices"] if d["deviceId"] == ids[BR])
check("a title that never answers is reported", dev_br["unread"] == 1, str(dev_br))
check("...and keeps its last known files (not 'not in library')", ka in br_cells(), str(sorted(br_cells())))
with urllib.request.urlopen(RELAY + "/api/progress/diagnostics?q=zzz", timeout=20) as r:
    check("...and is named on the diagnostics page", "Couldn't be read" in r.read().decode())
mock(BR, "flaky", every=0, fail="")

# ---------------------------------------------------------------- 29. diagnostics compares the library walls
with urllib.request.urlopen(RELAY + "/api/progress/diagnostics", timeout=20) as r:
    diag = r.read().decode()
check("diagnostics names posters that differ between walls", "poster(s) differ between players" in diag and "Collection: Sci-Fi Collection" in diag
      and "Home Theater Room 0" in diag, diag[:500])

# ---------------------------------------------------------------- 30. power off never flashes back to "on"; Launch HT waits for the app
http("POST", RELAY + "/api/devices/" + urllib.parse.quote(ids[HT]) + "/power", {"on": False})
seen, t0 = [], time.time()
while time.time() - t0 < 40:
    pw = dev(HT)["power"]
    if not seen or seen[-1] != pw: seen.append(pw)
    if pw == "off": break
    time.sleep(0.3)
check("power off goes straight from turning off to off", seen[-1] == "off" and "on" not in seen[1:], str(seen))
http("POST", RELAY + "/api/devices/" + urllib.parse.quote(ids[HT]) + "/power", {"on": True})
wait_for(lambda: dev(HT)["power"] == "on" and dev(HT)["homeTheater"] is True, 60)
mock(HT, "htclosed", on=1)
wait_for(lambda: dev(HT)["homeTheater"] is False, 40)
t0 = time.time()
http("POST", RELAY + "/api/devices/" + urllib.parse.quote(ids[HT]) + "/launch")
took = time.time() - t0
check("Launch HT returns only once the app answers", took >= 1.5 and dev(HT)["homeTheater"] is True, f"{took:.1f}s, {dev(HT)['homeTheater']}")

# ---------------------------------------------------------------- 31. Plex: sign in, server, folders, users
PLEX, PLEXTV = "http://127.0.0.6:32400", "http://127.0.0.7:32400"
def plexmock(path, **q):
    return http("GET", f"{PLEX}/_test/{path}?" + urllib.parse.urlencode(q))
def plexitem(user, key):
    return plexmock("item", user=user, key=key)
settings(watchOnly=False)
pin = http("POST", RELAY + "/api/plex/signin")
check("Plex sign-in gives a code for plex.tv/link", pin["code"] == "RRPX" and pin["linkUrl"] == "https://plex.tv/link", str(pin))
check("not signed in until the code is entered", http("GET", RELAY + "/api/plex/signin")["done"] is False)
http("GET", PLEXTV + "/_test/link")
check("signed in once the code is entered", wait_for(lambda: http("GET", RELAY + "/api/plex/signin")["done"], 10))
servers = http("GET", RELAY + "/api/plex/servers")
check("only servers are listed (not players)", [x["name"] for x in servers] == ["CHETPLEX"], str(servers))
srv = http("POST", RELAY + "/api/plex/server", {"clientId": servers[0]["clientId"]})
check("server reached on its local address, not the relay", srv["url"] == PLEX, str(srv))
sug = http("GET", RELAY + "/api/plex/suggest")
check("folder mapping suggested from a file a Zidoo played", sug["suggested"] and sug["suggested"][0] == {"plexFolder": "/shares/Entertainment", "zidooFolder": SHARE}, str(sug))
http("POST", RELAY + "/api/plex/mapping", {"mappings": sug["suggested"][:1]})
t = http("POST", RELAY + "/api/plex/test")
check("mapping test: every Plex file is covered and a Zidoo finds them", t["mapped"] == t["total"] == 4 and t["asked"] > 0 and t["found"] == t["asked"], str(t))
users = http("GET", RELAY + "/api/plex/users")
check("Plex Home users are listed", [u["title"] for u in users] == ["chet", "Kid"], str(users))
http("POST", RELAY + "/api/plex/users", {"uuid": "uuid-chet"})
try:
    http("POST", RELAY + "/api/plex/users", {"uuid": "uuid-kid", "pin": "0000"})
    bad = None
except urllib.error.HTTPError as e:
    bad = json.loads(e.read().decode())
check("a wrong PIN is refused", bad and "PIN" in bad["error"], str(bad))
http("POST", RELAY + "/api/plex/users", {"uuid": "uuid-kid", "pin": "1234"})
st = http("GET", RELAY + "/api/state")
pdev = [d for d in st["devices"] if d["kind"] == "plex"]
PO = next(d["id"] for d in pdev if d["plexUser"] == "chet")
PK = next(d["id"] for d in pdev if d["plexUser"] == "Kid")
check("Plex users become players", len(pdev) == 2 and all(d["name"].endswith(" on Plex") for d in pdev), str(pdev))
check("tokens never reach the page", "srv-owner" not in json.dumps(st) and "acct-owner" not in json.dumps(st))
check("Plex users show as on", wait_for(lambda: all(d["online"] for d in http("GET", RELAY + "/api/state")["devices"] if d["kind"] == "plex"), 15))
keys = plexmock("keys")
P_ARR, P_E03 = keys["Arrival (2016).mkv"], keys["Andor - S01E03.mkv"]

# ---------------------------------------------------------------- 32. Zidoo → Plex
play_then_stop(LR, ARRIVAL, 2222000)
check("a Zidoo stop is copied to each Plex user", wait_for(lambda: plexitem("1001", P_ARR)["viewOffset"] == 2222000 and plexitem("1002", P_ARR)["viewOffset"] == 2222000, 30),
      str(plexitem("1001", P_ARR)))
zc = wait_for(lambda: (lambda c: c if c and all(t["state"] != "Pending" for t in c["targets"]) else None)(card_for("Arrival", 2222000)), 20)
check("the card says the stop was on a Zidoo, and which targets are Plex",
      zc and zc["fromKind"] == "zidoo" and {t["name"]: t["kind"] for t in zc["targets"]}.get("chet on Plex") == "plex", str(zc))
play_then_stop(LR, E03, 2350000)
check("a finished episode is marked played on Plex", wait_for(lambda: plexitem("1001", P_E03)["viewCount"] >= 1 and plexitem("1001", P_E03)["viewOffset"] == 0, 30), str(plexitem("1001", P_E03)))

# ---------------------------------------------------------------- 33. Plex → Zidoos
plexmock("play", user="1001", key=P_ARR, pos=3000000)
check("what a Plex user is playing shows on their card",
      wait_for(lambda: (lambda d: d["playing"] and d["playing"]["title"] == "Arrival")(next(d for d in http("GET", RELAY + "/api/state")["devices"] if d["id"] == PO)), 15))
plexmock("stop", user="1001", final=3012000)
check("a Plex stop is copied to the Zidoos at the exact saved time", wait_for(lambda: bookmark(LR, ARRIVAL)["playPoint"] == 3012000 and bookmark(BR, ARRIVAL)["playPoint"] == 3012000, 30),
      str((bookmark(LR, ARRIVAL)["playPoint"], bookmark(BR, ARRIVAL)["playPoint"])))
check("...and to the other Plex user", wait_for(lambda: plexitem("1002", P_ARR)["viewOffset"] == 3012000, 20), str(plexitem("1002", P_ARR)))
pc = wait_for(lambda: card_for("Arrival", 3012000), 20)
check("the card says the stop was on Plex", pc and pc["fromKind"] == "plex" and pc["fromName"] == "chet on Plex", str(pc))
plexmock("play", user="1002", key=P_ARR, pos=6500000)
time.sleep(4)
plexmock("stop", user="1002", final=6700000)
check("finishing on Plex (it marks it played) marks it watched on the Zidoos",
      wait_for(lambda: bookmark(LR, ARRIVAL)["playPoint"] >= 6900000 or bookmark(LR, ARRIVAL).get("watched"), 30), str(bookmark(LR, ARRIVAL)))

# ---------------------------------------------------------------- 34. resets, Progress Reset and Watch Sync with Plex
plexmock("set", user="1001", key=P_ARR, pos=1800000)
fc = wait_for(lambda: card_for("Arrival", 3012000), 5)
http("POST", RELAY + f"/api/events/{fc['id']}/reset")
check("Reset Progress clears Plex too", wait_for(lambda: plexitem("1001", P_ARR)["viewOffset"] == 0 and plexitem("1001", P_ARR)["viewCount"] == 0, 30), str(plexitem("1001", P_ARR)))
plexmock("set", user="1001", key=P_E03, pos=900000)
plexmock("set", user="1002", key=P_E03, pos=0, count=1)
http("POST", RELAY + "/api/progress/read", {"deviceIds": list(ids.values()) + [PO, PK]})
wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 60)
prog = http("GET", RELAY + "/api/progress")
pf = {f["key"]: f for f in prog["files"]}
check("Progress Reset reads Plex users as a column", k3 in pf and pf[k3]["cells"].get(PO, {}).get("playPointMs") == 900000, str(pf.get(k3)))
w = {f["key"]: f for f in http("GET", RELAY + f"/api/watched?show=all&players={urllib.parse.quote(PO + ',' + PK)}")["files"]}
check("Watch Sync shows Plex's played mark", w.get(k3, {}).get("cells", {}).get(PK, {}).get("watched") is True, str(w.get(k3)))
r = http("POST", RELAY + "/api/watched/set", {"key": k3, "deviceIds": [PO], "watched": True})
check("Watch Sync marks played on Plex", r and r[0]["ok"] and plexitem("1001", P_E03)["viewCount"] >= 1, str(r))
r = http("POST", RELAY + "/api/watched/set", {"key": k3, "deviceIds": [PK], "watched": False})
check("Watch Sync marks unplayed on Plex", r and r[0]["ok"] and plexitem("1002", P_E03)["viewCount"] == 0, str(r))
plexmock("set", user="1002", key=P_E03, pos=500000)
http("POST", RELAY + "/api/progress/reset-series", {"key": k3})
check("Reset TV Show clears the show on Plex with one 'mark as unplayed'",
      wait_for(lambda: any(x["path"] == "/:/unscrobble" and x["key"] >= 900 for x in plexitem("1002", P_E03)["writes"]) and plexitem("1002", P_E03)["viewOffset"] == 0, 30),
      str(plexitem("1002", P_E03)))

# ---------------------------------------------------------------- 35. badge icons
req = urllib.request.Request(RELAY + "/api/icon/plex", data=b"\x89PNG\r\n\x1a\nfake", method="POST", headers={"Content-Type": "image/png"})
urllib.request.urlopen(req, timeout=10)
check("a custom badge icon is saved and served", http("GET", RELAY + "/api/state")["icons"]["plex"] is True
      and urllib.request.urlopen(RELAY + "/api/icon/plex", timeout=10).read(4) == b"\x89PNG")
http("DELETE", RELAY + "/api/icon/plex")
check("...and can be removed", http("GET", RELAY + "/api/state")["icons"]["plex"] is False)

# ---------------------------------------------------------------- 36. Plex sessions that list the owner differently
plexmock("play", user="1001", key=P_ARR, pos=1500000, uid="98765", utitle="chet", nofile=1)
check("the owner is recognised by name, and the file is read from the item when the session leaves it out",
      wait_for(lambda: (lambda d: d["playing"] and d["playing"]["title"] == "Arrival")(next(d for d in http("GET", RELAY + "/api/state")["devices"] if d["id"] == PO)), 15))
with urllib.request.urlopen(RELAY + "/api/plex/sessions", timeout=20) as r:
    ptxt = r.read().decode()
check("the Plex troubleshooting page names the session's user and match", 'name "chet" -> matches chet on Plex' in ptxt and "srv-owner" not in ptxt, ptxt[:600])
plexmock("stop", user="1001")

# ---------------------------------------------------------------- 37. Progress Reset: copy one player's progress to others (drag + Apply)
now = int(time.time() * 1000)
mock(LR, "setlwt", url=E03, lwt=now, pp=777000)
mock(BR, "setlwt", url=E03, lwt=now, pp=0)
http("POST", RELAY + "/api/progress/read", {"deviceIds": [ids[LR], ids[BR], PO]})
wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 60)
r = http("POST", RELAY + "/api/progress/copy", [{"key": k3, "from": ids[LR], "to": [ids[BR], PO]}])
check("Apply queues one copy per player it was dragged to, and names its Activity card", r["copies"] == 2 and r["cards"] and r["cards"][0]["cardId"], str(r))
check("the dragged progress lands on a Zidoo", wait_for(lambda: bookmark(BR, E03)["playPoint"] == 777000, 30), str(bookmark(BR, E03)["playPoint"]))
check("...and on Plex", wait_for(lambda: plexitem("1001", P_E03)["viewOffset"] == 777000, 30), str(plexitem("1001", P_E03)))
cc = wait_for(lambda: next((e for e in events() if e.get("copied") and e["fromName"] == "Living Room" and e["positionMs"] == 777000), None), 10)
check("the copy gets an Activity card from the source player", cc and {t["name"] for t in cc["targets"]} == {"Bedroom", "chet on Plex"}, str(cc))
check("the copied cells update without reading again",
      wait_for(lambda: (lambda f: f and f["cells"].get(ids[BR], {}).get("playPointMs") == 777000)(next((f for f in http("GET", RELAY + "/api/progress")["files"] if f["key"] == k3), None)), 20))

# ---------------------------------------------------------------- 38. a collection that answers "busy" is asked again
mock(BR, "setlwt", url=ARRIVAL, lwt=int(time.time() * 1000), pp=1234000)
mock(BR, "flaky", every=0, collfail=3)
http("POST", RELAY + "/api/progress/read", {"deviceIds": [ids[BR]]})
wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 60)
af = next((f for f in http("GET", RELAY + "/api/progress")["files"] if f["key"] == ka), None)
check("a busy collection is asked again and its movie is read", af and af["cells"].get(ids[BR], {}).get("playPointMs") == 1234000, str(af))

# ---------------------------------------------------------------- 39. Watch Sync: Watched All / Unwatched All on a TV show
http("POST", RELAY + "/api/progress/read", {"deviceIds": [ids[LR], ids[BR], PO]})
wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 60)
three = [ids[LR], ids[BR], PO]
dry = http("POST", RELAY + "/api/watched/set-show", {"show": "Andor", "deviceIds": three, "watched": True, "dryRun": True})
check("Watched All counts the show's episodes first, without changing anything", dry["episodes"] == 2 and dry["changes"] > 0
      and not bookmark(LR, E04).get("watched"), str(dry))
http("POST", RELAY + "/api/watched/set-show", {"show": "Andor", "deviceIds": three, "watched": True, "dryRun": False})
def andor_cells():
    fs = {f["key"]: f for f in http("GET", RELAY + "/api/watched?show=all&players=" + urllib.parse.quote(",".join(three)))["files"]}
    return [fs[k]["cells"].get(d, {}).get("watched") for k in (k3, k4) for d in three if k in fs and d in fs[k]["cells"]]
check("Watched All marks every episode watched on every ticked player", wait_for(lambda: (lambda c: len(c) == 6 and all(c))(andor_cells()), 60), str(andor_cells()))
http("POST", RELAY + "/api/watched/set-show", {"show": "Andor", "deviceIds": three, "watched": False, "dryRun": False})
check("Unwatched All clears them again", wait_for(lambda: (lambda c: len(c) == 6 and not any(c))(andor_cells()), 60), str(andor_cells()))

# ---------------------------------------------------------------- 40. finished = watched with no resume point, everywhere
def clean_watched(ip, url):
    b = bookmark(ip, url)
    return b.get("watched") and b["playPoint"] <= 0
for ip in (LR, BR, HT):
    mock(ip, "setlwt", url=E04, lwt=-1, pp=0)
play_then_stop(LR, E04, 2800000)   # 48:20 long: stopped in the last minutes, so it counts as finished
check("finishing on a Zidoo marks the other Zidoos watched with no resume point",
      wait_for(lambda: clean_watched(BR, E04) and clean_watched(HT, E04), 30), str((bookmark(BR, E04), bookmark(HT, E04))))
check("...and clears the leftover point on the player it was finished on", wait_for(lambda: clean_watched(LR, E04), 30), str(bookmark(LR, E04)))
mock(BR, "setlwt", url=E03, lwt=int(time.time() * 1000), pp=2350000, watched=1)   # an old near-the-end stop
http("POST", RELAY + "/api/progress/read", {"deviceIds": [ids[LR], ids[BR], PO]})
wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 60)
td = http("POST", RELAY + "/api/progress/tidy", {"deviceIds": [ids[LR], ids[BR], PO], "dryRun": True})
check("the cleanup counts watched files that kept a resume point", td["changes"] >= 1 and not clean_watched(BR, E03), str(td))
http("POST", RELAY + "/api/progress/tidy", {"deviceIds": [ids[LR], ids[BR], PO], "dryRun": False})
check("the cleanup leaves them watched with no resume point", wait_for(lambda: clean_watched(BR, E03), 30), str(bookmark(BR, E03)))
check("...and they leave the Progress Reset list", wait_for(lambda: not any(f["key"] == k3 and f["cells"].get(ids[BR], {}).get("playPointMs", 0) > 0
                                                                          for f in http("GET", RELAY + "/api/progress")["files"]), 20))
mock(BR, "setlwt", url=E03, lwt=int(time.time() * 1000), pp=2350000, watched=1)
http("POST", RELAY + "/api/progress/read", {"deviceIds": [ids[BR]]})
wait_for(lambda: not http("GET", RELAY + "/api/progress")["reading"], 60)
r = http("POST", RELAY + "/api/watched/set", {"key": k3, "deviceIds": [ids[BR]], "watched": True})
check("Watch Sync's Watched also clears a leftover point on an already watched file", r[0]["ok"] and clean_watched(BR, E03), str((r, bookmark(BR, E03))))

passed = sum(1 for _, ok in results if ok)
print(f"\n{passed}/{len(results)} passed")
sys.exit(0 if passed == len(results) else 1)
