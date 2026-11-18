# Config and launch — 26642252

Instance id: `matrix-26642252` (per-instance config/hot/screens/log; no shared state with the pin or the
other test copy).

Launch (Task Scheduler, interactive session 1, logon type "interactive token", no execution time
limit; **not** `win/run-game.ps1`):

```
C:\AlterumTests\26642252\Alterum.exe -screen-fullscreen 0 -screen-width 640 -screen-height 360 -alterumInstance=matrix-26642252
```

`BepInEx/config/com.user0.alterum.modelswap.matrix-26642252.cfg`:

```
[Automation]
ClickSequence = Create Lobby Menu Button|@LobbyPrivate|Lobby Creator/Vertical Sectors/Buttons/Gothic
ClickFirstDelaySeconds = 7
ClickStepDelaySeconds = 3
[Debug]
Screenshots = false
[Mirror]
AutoPlace = true
LobbyOnly = true
ShowLocalPlayer = false
[Window]
Quiet = false
ForceWindowed = true
QuietWidth = 640
QuietHeight = 360
```

`BepInEx/config/com.user0.alterum.lobby.matrix-26642252.cfg`: `StartWithOnePlayer = true` (needed because
both shipped maps declare `min=4` and this is a single-player harness).

Emulator: gbe_fork `steam_api64.dll` (11,431,336 B, copied from the pin) + `steam_settings/` with a unique identity (real Steam client is not running). Emote pack
`BepInEx/plugins/AlterumEmotes/` (emote.bin + manifest.txt, copied from the pin). Reel store seeded
from the pin's `alterum-reel-placements.json` (format v2, 2 anchors on Lair of Occultists).

Applied cfg copies are in `logs/*.cfg`.
