# Resume Relay

**Stop a movie or episode on one Zidoo, walk to another room, and press Resume.**

Resume Relay watches every Zidoo player on your network. A few seconds after one stops, it copies
that file's resume point to all the others. You don't need a watch list or any setup per title:
anything you watch is copied.

### ⬇ [Download the latest version](https://github.com/ckenisell/resume-relay/releases/latest)

On that page, scroll down to **Assets** and pick your file:

| | File | |
|---|---|---|
| **Windows 10 / 11** | `ResumeRelay-<version>-windows.exe` | A single file with nothing else to install. Put it somewhere permanent (for example your Documents) and run it. |
| **Ubuntu 22.04 / 24.04 / 26.04** | `resume-relay-<version>-linux.tar.gz` | A background service, ideal for an always-on PC or server. Unpack it and run `./install.sh`. |
| **Docker / NAS** (Synology, uGreen and others) | `ghcr.io/ckenisell/resume-relay` | No download needed: your NAS fetches it. See [Docker and NAS](#docker-and-nas-synology-ugreen-and-others) below. |

Ignore the two "Source code" files; they're the raw code, not the app. Run only **one** copy of
Resume Relay per home, on a computer that stays on while you watch.

## What it does

- **Works with Plex too.** Sign in with Plex in Settings and add your Plex users: resume points and watched
  marks then sync between the Zidoos and Plex, in both directions.
- **Copies resume points between rooms automatically.** Within seconds of stopping, the same file
  on every other player resumes where you left off, to the exact second. Movies, TV episodes and
  extras are all matched exactly by file path.
- **The latest stop always wins.** It replaces the other players' bookmark for that file, even if
  theirs is further along. Short stops count too.
- **Finishing marks it watched everywhere.** Auto-play to the next episode counts as finishing the
  previous one.
- **Stopping at 0:00 resets the title everywhere.** Start something from the beginning and stop it
  straight away, and the other players clear their bookmark and watched mark too.
- **Reset Progress from the Activity tab.** One button clears a file's bookmark and watched mark on
  every player.
- **Progress Reset tab.** Read your players to see every file you've started, how far in it is on each
  player and when it was last watched, sorted by title or date. Reset one file, or every episode of a
  TV show, on all players at once.
- **Watch Sync tab.** See where each movie and episode is marked watched on every player, find the ones
  that differ, and mark them watched or unwatched per player or on all players at once.
- **Sleeping players catch up.** A player that's off, asleep, or on without its Home Theater app
  open gets its copies as soon as it's ready. Waiting copies are shown on that player's card and
  kept for 14 days (you can change this in Settings).
- **Turn players on and off.** Each player has a power button like Z-Keeper's: red is off, green is
  on, and an orange ring spins while it's switching. **Launch HT** opens the Home Theater app.
- **Copy all, then undo if needed.** On Progress Reset and Watch Sync, copy one player's progress or watched marks to
  the others for every file in the list, and undo any batch from its Activity card.
- **Tells you when there's a new version,** with the exact steps to update.
- **Catches up after downtime.** If Resume Relay was off, it reads the players when it starts and copies whatever
  was watched meanwhile (newest wins), with Undo.
- **Easy to set up and move.** A getting-started checklist for new installs, health notes when something needs
  attention, and a one-file backup of your players and settings.
- **See every copy.** The Activity tab shows a card for each stop, with the poster, whether it happened on a Zidoo
  or on Plex, and a mark for every other player: ✓ copied, ◷ waiting, – skipped, ✕ failed.
- **Every write is checked.** Before writing, it confirms the player's entry really is that file.
  After writing, it reads the position back, because some Home Theater builds report errors for
  writes that actually worked.
- **Finds your players for you.** Scan the network, then give each player a room name. Players are
  recognised by their hardware address, so a name stays put even if your router gives the player a
  new IP.
- **Use it from your phone.** Open `http://<computer-address>:8090` on any device on your network.

## Requirements

- One of:
  - **Windows 10 or 11**
  - **Ubuntu 22.04, 24.04 or 26.04**
  - **Docker**, for example on a **Synology** NAS (DSM 7.2 or later, with Container Manager), a
    **uGreen** NAS (UGOS Pro, with the Docker app), QNAP, Unraid or TrueNAS. Intel/AMD and ARM
    (64-bit) are both supported. The container must use **host networking**.
- Zidoo players with **Home Theater v5.1.11 (code 5111) or newer**
- Optional: a **Plex Media Server** that reads the same network share as the Zidoos
- Your video files on a network share (SMB or NFS) that all players use, so each file has the same
  path everywhere
- To turn players on from the app (Wake-on-LAN): the player in standby (not unplugged), on a
  **wired** connection, with network wake-up allowed in its settings

## First run

1. Press **Scan network**. Every Zidoo that's switched on is listed.
2. Click each player's name and give it a room name.
3. It starts in **Watch-only** mode, which shows what it would copy without writing anything.
   Play something on one player, stop it, and check **Activity**. When it looks right, press
   **Start copying**.

## Plex

The **Plex** card in **Settings** (the gear tab) sets it up:

1. **Sign in with Plex.** Enter the code it shows at [plex.tv/link](https://plex.tv/link) on any device signed in to
   Plex. Your Plex server is found by itself.
2. **Folders.** Plex and the Zidoos reach the same files by different paths (for example `/shares/Entertainment` on
   the Plex server and `smb://10.0.0.56/Entertainment` on the Zidoos). Once a Zidoo has played something Plex also
   has, the card fills these in for you. Press **Test** to check them.
3. **Add users.** The server's owner and any Plex Home users (with their PIN if they have one). Each becomes a player
   card on the Players tab.

Plex counts a file as finished at about 90% (its default), so finishing in Plex marks it watched on the Zidoos. If
Plex doesn't keep a very early resume point (a stop in the first minute), the Activity chip says so.

## Live Poster

A TV, stick or tablet hung outside a room shows the poster of what that room is playing, over a background of your
own design ("NOW SHOWING"…), and a slideshow when nothing is: **Coming Soon** (posters you add), **Recently Watched**
and **Highly Acclaimed** (movies and series rated 8.0 or higher). Set it all up on the **Live Poster** tab.

- **4K:** stick and TV apps draw their own pictures at 1080p but play video at 4K, so Resume Relay turns each picture
  into a short 4K video (with ffmpeg, on the computer's video hardware when it can) and the screen app plays it.
- **Posters:** movies come from the movie's folder (`<video file name>-poster.jpg`, `poster.jpg`…), then Plex, then the
  Zidoo (which keeps them at 500×750). TV shows: the season poster in the show's folder (`season01-poster.jpg`,
  `Show S01-poster.jpg`, or `poster.jpg` inside a `Season 01` folder), then the series poster, then Plex's, then the
  Zidoo's.
- **Backgrounds:** 2160×3840 PNG/JPG/WebP or a 10–20 second MP4 loop. The poster sits in the middle at 2000×3000, which
  leaves 80 px at the sides and 420 px at the top and bottom.
- **Screen apps** (beta): Android (Fire TV, Google TV, phones and tablets) and Roku, from the release's downloads, or
  any web browser at `http://<this computer>:8090/screen`. Until they're in the app stores:
  - **Roku:** turn on Developer Mode (on the remote: Home 3×, Up 2×, Right, Left, Right, Left, Right; set a password),
    open the Roku's address in a browser on your computer, and upload `ResumeRelay-Screen-…-roku.zip`.
  - **Android phone or tablet:** open the `…-android.apk` download on it and allow installing it.
  - **Fire TV / Google TV:** install it with the *Downloader* app (enter the APK's download link).
- **Mounting a TV upright:** TVs and sticks always send a landscape picture, so tell Resume Relay which way the TV is
  turned and check the **Test picture** (the arrow should point up).
- Avoid TVs that can get burn-in (OLED, plasma): a poster can stay up for hours.
- **Docker:** pass the video hardware through (`devices: - /dev/dri:/dev/dri`) for hardware encoding; screen apps find
  Resume Relay with a broadcast on UDP port 47090 (host networking passes it through).

## Windows app

- Opens in its own window. Closing the window keeps it running in the tray, so bookmarks keep
  syncing. Right-click the tray icon to quit.
- **Starts with Windows** by default, straight to the tray. Untick it in Settings or the tray menu.
- The first time you run it, Windows may show *"Windows protected your PC"* because the app isn't
  code-signed. Click **More info → Run anyway**. If Windows asks about network access, allow
  **Private networks**.
- Needs Microsoft Edge WebView2, which is built into Windows 10 and 11.

## Linux service

Unpack the download and run `./install.sh`. It installs the .NET runtime from Ubuntu's own archive
if needed and sets Resume Relay up to start with the computer. Upgrading keeps your players and
settings. Full instructions are in the `README.md` inside the download.

## Docker and NAS (Synology, uGreen and others)

The Docker image is the same program as the Windows and Ubuntu downloads, so it works exactly the
same way. It runs on Intel/AMD and ARM (64-bit) NAS models. Run only one copy of Resume Relay per
home: if you move it to your NAS, quit or uninstall it on your PC.

Everything is set up from this short "docker-compose" text. You'll paste it in during setup:

```yaml
services:
  resume-relay:
    image: ghcr.io/ckenisell/resume-relay:latest
    container_name: resume-relay
    # Required: lets "Scan network" find your players and the power button wake them.
    network_mode: host
    restart: unless-stopped
    environment:
      # The page opens at http://<nas-address>:8090. Change it if 8090 is already used on the NAS.
      - RESUMERELAY_PORT=8090
    volumes:
      # Players, names, settings and Activity are kept here, next to this file.
      - ./data:/data
```

**Keep `network_mode: host`.** Without it the container sits on Docker's own private network inside
the NAS: talking to players by IP address still works, but **Scan network** finds nothing and the
**power button** can't turn players on.

### Synology (DSM 7.2 or later)

1. Open **Package Center**, search for **Container Manager** and install it. If it isn't listed,
   your model doesn't support Docker.
2. Open **File Station**. In the **docker** shared folder (Container Manager creates it), create a
   folder named **resume-relay**.
3. Open **Container Manager → Project → Create**.
   - **Project name:** `resume-relay`
   - **Path:** choose the **docker/resume-relay** folder you just made
   - **Source:** **Create docker-compose.yml**, then paste the text above
4. Click **Next**, skip the Web Station page, and click **Done**. Container Manager downloads the
   image and starts it.
5. Open `http://<your-nas-address>:8090` in a browser and follow [First run](#first-run).

If the DSM firewall is on (**Control Panel → Security → Firewall**), add a rule allowing TCP port
8090, or the page won't open from other devices.

**Updating:** in **Container Manager → Image**, `ghcr.io/ckenisell/resume-relay` shows **Update
available** when a new version is out. Click it and choose **Update**. Your players and settings are
kept in the **data** folder.

### uGreen (UGOS Pro)

1. Open **App Center**, install **Docker**, and open it.
2. Go to **Project → Create**.
   - **Project name:** `resume-relay`
   - **Storage path:** choose or create a folder for it, for example **docker/resume-relay**
   - **Compose configuration:** paste the text above
3. Click **Deploy**. Docker downloads the image and starts it.
4. Open `http://<your-nas-address>:8090` in a browser and follow [First run](#first-run).

**Updating:** pull the newest `ghcr.io/ckenisell/resume-relay:latest` image (Docker → **Image**),
then redeploy the **resume-relay** project. Your players and settings are kept in its **data**
folder. Menu names can differ slightly between UGOS versions.

### Other NAS and Docker hosts (QNAP, Unraid, TrueNAS, Portainer, command line)

Use the same docker-compose text, for example as a Portainer **Stack**, a QNAP Container Station
**Application**, or from a terminal in an empty folder:

```
docker compose up -d                          # start
docker compose pull && docker compose up -d   # update
```

Without compose, the equivalent is:

```
docker run -d --name resume-relay --network host --restart unless-stopped \
  -v /path/on/nas/resume-relay:/data ghcr.io/ckenisell/resume-relay:latest
```

## Known limitations

- Resume points are copied when playback stops. Anything watched while Resume Relay itself isn't
  running isn't copied afterwards.
- Files played from a USB drive or a player's internal disk can't be matched across players, so
  they aren't copied.

## Special Thanks

- Thanks to Chris at Z-Keeper.com for leading the way with his Z-Keeper app and for sharing his knowledge with me.
- Thanks to Zidoo for working with Chris to update the Zidoo API with the appropriate endpoints that allow this app to work.

The Zidoo API knowledge behind this project comes largely from the HT5 API guide at
[z-keeper.com](https://z-keeper.com/learn/ht5-api-guide). Not affiliated with Zidoo.

## For developers

```
dotnet publish src/ResumeRelay/ResumeRelay.csproj -c Release                  # Linux service
dotnet publish src/ResumeRelay.Windows/ResumeRelay.Windows.csproj -c Release  # Windows app (on Windows)
```

`test/mock_zidoo.py` simulates three players. `test/run_tests.py` runs the end-to-end checks
against them (see `.github/workflows/build.yml`).

## License

MIT. See [LICENSE](LICENSE). You are free to use, change and include this code in your own projects.
