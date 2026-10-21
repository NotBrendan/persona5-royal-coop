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

## 2026-10-20: phase 2 start (battle hook research)

### Observations
- In P5R the player already picks every party member's command (Direct Commands), so co-op can work at the **input level**:
  during a friend's character's turn, block the host's input and feed the friend's input instead. No need to decode commands.
- p5rpc.inputhook (AnimatedSwine37, GPL-3.0): Reloaded library, `IInputHook` with `OnInput`, `OnInputIntercept` (edit the
  key list the game sees) and `SendKey(Key)`. Keyboard only, rising-edge only. NuGet `p5rpc.inputhook.interfaces`.
  https://github.com/AnimatedSwine37/p5rpc.inputhook
- p5rpc.lib (AnimatedSwine37): flowscript calls + sequence info; signatures updated for P5R 1.0.4 in its 1.0.5 release.
  https://github.com/AnimatedSwine37/p5rpc.lib
- No public mod found that exposes "which battle unit is acting now". That is the one thing to reverse engineer.

### Derived
- Remaining unknown for phase 2: the address/function that says which party member's command menu is open.
- Licensing: building on inputhook (GPL-3.0) means this mod should be GPL-3.0 compatible.

## 2026-10-20: phase 2 result: acting-unit value found

### Observations (TurnScan, live battle on the owner's PC, P5R 1.0.4.0)
- Scanned at each party member's command menu: Morgana(3) -> Ann(4) -> Ryuji(2) -> Joker(1) -> Morgana(3).
  Candidate counts: 11.5M -> ~2,900 -> 2 (4-byte) / 11 (2-byte) -> 1 -> 1.
- **One u16 matched every step**, in heap memory (address changes per battle/session, so no fixed offset yet).
  Unit ids confirmed: 1 Joker, 2 Ryuji, 3 Morgana, 4 Ann (the rest of the list is still assumed).
- Two 4-byte candidates matched 3,4,2 but not Joker's 1: a decoy, not the turn value.
- Live watch for the rest of the battle: 3,4,2,1,3,4,2. The value **does not change during enemy turns and does
  not reset when the battle ends** (it stayed at the last party member). It means "last party member given the
  command menu", not "a party member's menu is open right now".
- Bytes around it (when value was 3): `... 00 00 80 3F 01 72 [03 00] 04 00 00 00 A0 FC 96 41 01 00 ...`.
  The u16 right after it was 4 (Ann, next to act) at that moment: possibly a turn queue. Unverified.

### Derived
- The mod needs a second signal: "the party command menu is open" (or "battle is active"), so friend input
  is only routed while the menu is actually waiting for input.
- The address must be reached from something stable: a pointer path from P5R.exe, or a hook on the code that writes it.

### Next
1. Find the code that writes the value (a hook point gives both signals at once), or a pointer path that survives battles.
2. Find the "command menu open" / "in battle" signal.

## 2026-10-20: battle 2 (same game session)

### Observations
- The turn value is **per battle**: battle 2 had a new copy at a different heap address. Battle 1's copy stayed readable
  with stale data (still 2), which briefly looked like a match. Do not reuse addresses across battles.
- Both copies sit in the same structure; bytes around the u16 (value marked [..]):
  `B0 83 E8 34 42 00 00 00 | 04 00 00 00 | 00 00 80 3F 01 02 [04 00] 06 00 00 00 A0 FC 96 41 01 00 00 00`.
  The leading 8 bytes look like a heap pointer; `00 00 80 3F` is float 1.0.
- TurnScan bug fixed: a 1 MB read failed as a whole when any page in it was unreadable, so the first scan of
  battle 2 missed the real copy. It now falls back to page-by-page reads.
- On a "1 More" (Ryuji again after hitting a weakness) the value stayed 2.
- Live watch around the value (offsets in decimal from the u16):
  - A byte at -10 went 04 -> 00 after Ann's guard, 00 -> 01 when the value became Ryuji's 2, then 01 -> 02 -> 04 -> 00
    about 4 s later, while Ryuji's menu was (per the owner) open afterwards with that byte at 0. Meaning unclear;
    **not** a plain "menu open" flag. The pointer at -18 is set/cleared together with it.
  - Bytes +474 and +510 toggle 0/1 around animations (attack, guard, enemy hit); +478..+505 are animated floats.
  - Nothing in -256..+512 changed when Ryuji's command was confirmed except the animation fields.

### Derived
- Watching nearby bytes is a slow way to find "menu waiting for input". Better next step: find the **code that writes
  the turn value** (hardware data breakpoint from inside the mod, logging RIP). That function is where the game
  opens a party member's command menu, so hooking it gives "turn started for unit N" directly, with no address hunting.
- Confirmed in battle 2: after the enemy's move the value switched 2 -> 1 the moment Joker's turn came up (22:08:30), and the -10 byte / -18 pointer cleared 250 ms later, the same pattern as after Ann's guard. Working guess: -18/-10 describe the action currently being executed (party or enemy), cleared when it finishes.
