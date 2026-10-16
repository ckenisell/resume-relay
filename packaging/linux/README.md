# Resume Relay @VERSION@

Stop a film or episode on one Zidoo, walk to another room, and press Resume.
Resume Relay watches every Zidoo player on your network and, a few seconds after one stops,
copies its resume point to the others. You don't need a watch list or any setup per title.

## Install on Ubuntu (22.04, 24.04 or 26.04)

```
tar -xzf resume-relay-@VERSION@-linux.tar.gz
cd resume-relay
./install.sh
```

The script asks for your password, installs Microsoft's .NET runtime from Ubuntu's own
archive if it's missing (.NET 10 on 26.04, .NET 8 on older releases), and sets Resume Relay up as a background service that starts with
the computer. When it finishes, it prints the address to open, e.g. `http://10.0.0.20:8090`.

Upgrading later: unpack the new version and run `./install.sh` again. Your players, names
and settings are kept.

## First run

1. Open the address in a browser and press **Scan network**. Every powered-on Zidoo is listed.
2. Click each player's name and type a room name (e.g. *Living Room*). Press Enter to save.
3. The app starts in **Watch-only** mode. It logs what it would copy, but writes nothing.
   Play something on one player for a minute or more and stop it. **Activity** should show
   "Would copy … from Living Room to Bedroom".
4. When that looks right, press **Start copying** at the top.

## How it decides

- The latest stop always wins. It replaces whatever bookmark the other players hold for
  that file, including a later position, and is copied however short it is (only 0:00 is
  skipped, because writing 0 would erase the bookmark).
- Players are matched by file path, so each film, episode and extra is matched exactly.
- Before writing, it checks that the player's entry really is that file. Afterwards, it
  reads the position back. A write only counts if the player shows it, because some HT
  builds report errors for writes that worked.
- A player that's asleep, or on without its Home Theater app open, gets the copy once it's
  ready. Waiting copies are listed on the page and kept for 14 days by default. If that
  player plays the same film itself in the meantime, the older waiting copy is discarded.
- Requires Zidoo Home Theater 5.1.03 or newer on every player.

## Useful commands

```
systemctl status resume-relay         # is it running?
journalctl -u resume-relay -f         # watch its log live
sudo systemctl restart resume-relay   # restart it
./uninstall.sh                        # remove it (keeps settings; add --purge to delete them)
```

Data lives in `/var/lib/resume-relay`. To use a port other than 8090, install with
`RESUMERELAY_PORT=9000 ./install.sh`.
