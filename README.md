# Bad Piggies :: Rebooted Edition
A Community-driven Modification of the game Bad Piggies!
Thanks to Miuna for all the prior work from the mod called BPLE, we really hope you are doing well and we await your return.

## Bad Piggies :: Rebooted Edition - Delta Epsilon
This branch of the mod development was run primarily by Goggs. This branch is the latest BP::RE version. It has some bugs though...

## Setup
The unity version to open this project with is [Unity 2021.3.45f2](https://unity.com/releases/editor/whats-new/2021.3.45f2).

## Branches
Here are a couple of major branches in the repo.

- **Delta Epsilon** (`delta-epsilon12345`) - Lead primarily by Goggs, this is the primary releases of Bad Piggies :: Rebooted.
- **Season Two** (`season-two`) - This branch was supposed to be the experimental Bad Piggies Rebooted 2x by me, but abandoned as Anstro could not fix the colored frame.
- **Anstro Editions** (`AnstroEdition`) - Used to be my branch of features and experiments.
- **OG BPLE** (`OG-BPLE`) - The initial(-ish) source code of BPLE 2022.1 decompiled by Dartn.
- **Fools Day 2024** (`foolsd-2024`) - 2024 April Fools version.
- **Anstro Tweaks** (`AnstroTweaks`) - An older branch of my features and experiments.

## Open Source???
No. This project is ***NOT*** Open Source. See the legal notice below. We are providing the source code to our mod in the hopes of software preservation, game's longevity and community extension.

Feel free to fork this repo and come up with your own modification. Let us know what you did with it at our discord server!

https://discord.gg/JYrbsXX

## Credits

- [Miuna](https://github.com/miu-na) for all the prior modding effort on Bad Piggies.
- [Goggs](https://github.com/Goggs77) - BP::RE project leader.
- [Anstro Pleuton](https://github.com/anstropleuton) - Some BP::RE features.
- Elderberry Starz - Many BP::RE custom assets.
- SaltedFish (DStuff) - Many BP::RE custom assets.
- [Creato](https://www.youtube.com/channel/UCJjkxlcWgDfMtm1WdIZnUBg) - Some BP::RE custom assets.
- Dartn - Initial decompilation of BPLE as the basis for BP::RE, project coordinator.
- [Egan](https://www.youtube.com/channel/UC71a3L1Lm-aDq5SK8owkBaA) - April fools custom assets.
- [snailmaster42](https://www.youtube.com/channel/UCudLJRGcDBTx9WO8NWoZ36A) - Some BP::RE custom assets.
- [Vas2000](https://www.reddit.com/user/Vas2000) - April fools splash screen, BP::RE custom assets.
- And counting...

## TODOs
These are left here to kick start development, though we may not do them ourselves.

- Fix broken physics (track weird changes relating to ECS integration by Goggs)
- Port Anstro Editions features to BP::RE (there are a lot)

Hot features that can be added:
- Brush tools
- Selection
- Move/delete selection
- Cut/copy/paste
- Delete confirmation
- Autosave
- Import/export
- Right-click menu for quick skin selection
- Contraption assets (save a chunk of contraption as placable artifact)
- Layer system
- Style redesign of BPLE (to look more like actual bad piggies)
- Port BPLE 2022.1.9 features

## Legal Notice
**This mod is not affiliated with Rovio in any way. Please note that the game Bad Piggies is developed by Rovio. Most of the unmodified source codes and resources belongs to them.**

## Multiplayer (experimental)
Press **F9** in the main menu (or type `mp` in the command interface) to open the multiplayer window.
One player hosts (TCP port 7777, forward it for internet play). Games on the same network appear
automatically in the join list (UDP port 7778); for other networks, or VPNs such as Tailscale, enter
`ip:port` by hand. The host plays as usual: whatever level the host loads, everybody follows. Every
player builds and drives their own contraption; the others are shown with name tags. Chat is built in.
While you are in a multiplayer level the game never pauses time, so your vehicle does not freeze
in mid-air for the others.

Modes (selected by the host):
- **Free Play** - build and drive together. The host can switch on *Solid contraptions* so vehicles collide.
- **Distance** - timed rounds; the pig that gets furthest (or highest) from the start wins.
- **Capture the Flag** - a flag spawns on the ground away from the start; touch it and bring it back to the start zone.
- **Battle** - other vehicles are solid once they leave the start zone; guns, TNT and ramming score points.

Console: `mp host [port]`, `mp join <address>`, `mp leave`, `mp mode <freeplay|distance|ctf|battle>`, `mp say <text>`.
Starter car for quick rounds (in a session, inside a level): **F7** builds a small car with engine, gearbox and motor wheels, **F8** starts or stops it, **F6** toggles the gearbox to reverse. The same buttons are in the F9 window.
Each vehicle is simulated only by its own player (the physics is not deterministic across machines), so
collisions between players can look slightly different on each screen. Code lives in `Assets/Scripts/Multiplayer`.

## Releases and automatic updates
Release builds update themselves. `Tools/Release.ps1` builds the player headless, and only if the build
succeeded it packages the game, signs the manifest and publishes it:

```
powershell -ExecutionPolicy Bypass -File Tools\Release.ps1 -Notes "What changed"
```

- Close the Unity editor for this project first.
- Output: `E:\bp-share\release\latest.json` plus `BadPiggiesRebooted-<build>.zip` (the last 3 are kept), and a
  copy as `E:\bp-share\BadPiggiesRebooted-Multiplayer.zip` for manual downloads. Paths and the feed URL
  (default `http://100.64.0.2:8088/release/`) are script parameters.
- The feed must be reachable over HTTP, e.g. `python -m http.server 8088 --bind <ip> --directory E:\bp-share`.
- A release build checks `latest.json` at start and every 30 minutes, downloads a newer build in the
  background, verifies checksum and signature, and installs it in the main menu after a 15 second
  countdown (*Later* postpones to the next start). Console: `update status|check|now|later`.
- Updates are signed with `%USERPROFILE%\.bpre\update-signing-key.xml`. Keep a backup of it and never
  commit it: without it, published games no longer accept updates. The public half lives in
  `Assets/Scripts/Updater/UpdateSigningKey.cs`.
- Builds made from the editor menu or `BPREDevTools.BuildFromCommandLine` are development builds and never update.
