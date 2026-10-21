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
