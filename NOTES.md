# Persona 5 Royal co-op notes

Running log. Observations, derivations, guesses and unknowns are kept apart.

## 2026-10-20: triage and research

### Game identity
- Store: Steam, app 1687950, build id 15515071
- Exe: `<game dir>\P5R.exe`, file version 1.0.4.0
- SHA-256: 5D9E18C383700302D029C947DDF8C811BA6AFA47E60EC1AFA0554D452BDC0CBF
- Engine: Atlus proprietary (native x64 C++), scripted by flowscript (`.bf`) and messagescript (`.bmd`)
- OS: Windows 11

### Observations
- No Persona entry in the Co-op Anything directory (local ZIP copy). No hub PRs mention Kenshi or Persona as of 2026-10-20.
- p5r.multiplayer (morgana-x): Reloaded-II mod plus a standalone UDP server. Syncs **player characters only**
  (other players appear as NPCs in the overworld). TODO: full animation set, chat, player verification,
  move off flowscript calls for position/rotation. 69 commits, 0 stars, no releases, license not shown.
  https://github.com/morgana-x/p5r.multiplayer
- A YouTube video titled "Persona 5 Royal Online Multiplayer - Live" exists; not checked which mod it shows.
  https://www.youtube.com/watch?v=MYUiBZqlLgk
- ZMenu (Zolika1351) is a mod menu + chaos mod for Persona 5, not co-op.
  https://zolika1351.pages.dev/mods/persona5menu
- The P5R modding ecosystem is Reloaded-II based (C#/.NET). This machine has no .NET SDK.

### Derived
- Overworld presence exists (p5r.multiplayer). **Nobody syncs battles, story, calendar or Palaces.**
- Turn-based battles are cheap to sync: send the chosen command per turn, not positions every frame.

### Decision (owner, 2026-10-20)
- Goal is **battle co-op**: host runs the game; each friend picks the commands for one party member.
  Outside battle, friends watch through a stream / Steam Remote Play. Overworld sync is out of scope for now.

### Unknown
- p5r.multiplayer's license, last commit date, and whether it builds against 1.0.4.0.
- Whether the battle system exposes a hook point for "who picks this party member's command".

## 2026-10-20: phase 1 setup (toolchain + smoke test)

### Observations
- Toolchain: .NET SDK 8.0.426, .NET Desktop Runtime 9.0.21 x64 **and x86** (Reloaded-II 1.31.0 refuses to start without the x86 one,
  even for 64-bit P5R; winget needs `--architecture x86 --force` because it treats the x64 install as satisfying it).
- Reloaded-II 1.31.0 Release.zip SHA-256 c893da3ba8596d9266826bd5ca8aacd25fe3b53d09314324ab65c7b618a05f8c matched the GitHub release digest.
- `Reloaded.Mod.Templates` 1.6.8 targets net9.0; retargeted the mod to net8.0-windows and it loads fine in Reloaded's .NET 9 runtime.
- Test copy of the game outside Steam's folder, with `steam_appid.txt` = 1687950. Reloaded's SteamHook also hooks
  SteamAPI_RestartAppIfNecessary and recommends that file.
- P5R stores saves in `%APPDATA%\SEGA\P5R\Steam\<steamid64>\savedata` (DATA01..), shared by any copy of the exe. Back them up before tests.
- **Smoke test passed:** the mod loaded into P5R.exe in 34 ms; image base 0x140000000, size 0x18133000. The game reached its window and kept responding.
- Launch headless with `Reloaded-II.exe --launch "<test dir>\P5R.exe"`; the launcher exits after injecting, which is expected, not a crash.
