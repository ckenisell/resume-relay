# Resume Relay

Stop a film or episode on one Zidoo player, walk to another room, and press Resume.

Resume Relay watches every Zidoo player on your network. A few seconds after one stops, it copies
that file's resume point to all the others. You don't need a watch list or any setup per title:
anything you watch is copied.

## Downloads

Get the latest version from the **Releases** page:

- **Windows:** `ResumeRelay-<version>-windows.exe`. One file with nothing to install alongside it.
  Put it somewhere permanent (for example `C:\Program Files\Resume Relay\` or your Documents) and run it.
- **Linux (Ubuntu 22.04, 24.04, 26.04):** `resume-relay-<version>-linux.tar.gz`. Runs as a
  background service; see the README inside.

## Windows app

- Opens in its own window. Closing the window keeps it running in the tray, so bookmarks keep syncing.
  Right-click the tray icon to quit.
- **Starts with Windows** by default, straight to the tray. Untick it in the app's Settings or the tray menu.
- The first time it runs, Windows may ask whether to allow it on your network. Allowing **private
  networks** lets you also open the page from your phone at `http://<pc-address>:8090`. The app
  works either way.
- Needs Microsoft Edge WebView2, which is built into Windows 10 and 11.

## First run

1. Press **Scan network**. Every Zidoo that's switched on is listed.
2. Click each player's name and give it a room name.
3. It starts in **Watch-only** mode, which logs what it would copy without writing anything.
   Play something on one player, stop it, and check **Activity**. When it looks right,
   press **Start copying**.

## How it decides

- The latest stop wins: it replaces the bookmark for that file on every other player.
- Players are matched by file path, so each film, episode and extra is matched exactly.
- Before each write it checks the player's entry really is that file. After the write it reads the
  position back, and only counts the copy if the player shows it.
- A player that's asleep, or on without its Home Theater app open, gets the copy once it's ready.

Requires Zidoo Home Theater **v5.1.11 (code 5111) or newer** on every player.

## Building

```
dotnet publish src/ResumeRelay/ResumeRelay.csproj -c Release          # Linux service
dotnet publish src/ResumeRelay.Windows/ResumeRelay.Windows.csproj -c Release   # Windows app (on Windows)
```

`test/mock_zidoo.py` simulates three players. `test/run_tests.py` runs the end-to-end checks
against them (see `.github/workflows/build.yml`).

The Zidoo API knowledge behind this project comes largely from the HT5 API guide at
[z-keeper.com](https://z-keeper.com/learn/ht5-api-guide). Not affiliated with Zidoo.
