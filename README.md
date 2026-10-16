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

Ignore the two "Source code" files; they're the raw code, not the app. Run only **one** copy of
Resume Relay per home, on a computer that stays on while you watch.

## What it does

- **Copies resume points between rooms automatically.** Within seconds of stopping, the same file
  on every other player resumes where you left off, to the exact second. Movies, TV episodes and
  extras are all matched exactly by file path.
- **The latest stop always wins.** It replaces the other players' bookmark for that file, even if
  theirs is further along. Short stops count too.
- **Finishing marks it watched everywhere.** Auto-play to the next episode counts as finishing the
  previous one.
- **Stopping at 0:00 resets the title everywhere.** Start something from the beginning and stop it
  straight away, and the other players clear their bookmark and watched mark too.
- **Sleeping players catch up.** A player that's off, asleep, or on without its Home Theater app
  open gets its copies as soon as it's ready. Waiting copies are shown on that player's card and
  kept for 14 days (you can change this in Settings).
- **Turn players on and off.** Each player has a power button like Z-Keeper's: red is off, green is
  on, and an orange ring spins while it's switching. **Launch HT** opens the Home Theater app.
- **See every copy.** The Activity tab shows a card for each stop, with the poster and a mark for
  every other player: ✓ copied, ◷ waiting, – skipped, ✕ failed.
- **Every write is checked.** Before writing, it confirms the player's entry really is that file.
  After writing, it reads the position back, because some Home Theater builds report errors for
  writes that actually worked.
- **Finds your players for you.** Scan the network, then give each player a room name. Players are
  recognised by their hardware address, so a name stays put even if your router gives the player a
  new IP.
- **Use it from your phone.** Open `http://<computer-address>:8090` on any device on your network.

## Requirements

- Zidoo players with **Home Theater v5.1.11 (code 5111) or newer**
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
