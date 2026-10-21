# Networking: battle co-op over a relay

How friends' key presses reach the host's game. Everything here builds and is tested on Linux
without the game; the game-specific part (which party member is acting) is behind an interface.

## Idea

In P5R the player already picks every party member's command. So co-op works at the **input level**:
while a friend-owned party member's command menu is open, the host's keyboard input is blocked and
that friend's key presses are injected instead (via p5rpc.inputhook). No battle command decoding.

```
 friend PC                         relay server (public)                    host PC (P5R + Reloaded-II)
┌──────────────────┐   TCP out   ┌──────────────────────┐   TCP out   ┌──────────────────────────────┐
│ P5RCoop.Friend   │────────────▶│ P5RCoop.Relay        │◀────────────│ p5rcoop.battle mod           │
│  console keys    │  KeyPress   │  rooms by join code  │  RelayData  │  Net/HostSession             │
│  FriendClient    │◀────────────│  forwards bytes only │────────────▶│   ├─ ITurnSource (game hook) │
└──────────────────┘ TurnChanged └──────────────────────┘             │   └─ IInputSink (inputhook)  │
        ×N friends    SeatAssignments                                  └──────────────────────────────┘
```

- Everyone connects **out** to the relay, so nobody needs port forwarding at home. The relay itself
  must be reachable (a small VPS, or one person who can open one port).
- The relay never parses game messages. The **host is authoritative** for seats, turns and input.
- A future Steam transport (Spacewar app 480, SteamNetworkingSockets relay) slots in as another
  `ITransport` / `IHostLink`; nothing above the transport changes. Not implemented.

## Projects

| Path | Target | What |
|---|---|---|
| `src/P5RCoop.Protocol` | net8.0 | Messages, binary serializer, key codes, relay frames, `RelayClient`, transports (`InMemoryTransport`, `TcpTransport`), `SequenceReorderBuffer`. No Windows dependencies. |
| `src/P5RCoop.Relay` | net8.0 exe | Relay server. `P5RCoop.Relay --port 7777 [--bind 0.0.0.0]` |
| `src/P5RCoop.Friend` | net8.0 exe | Friend app: join with code + name, claim a seat, see whose turn it is, send keys on your turn. |
| `src/p5rcoop.battle/Net/` | (mod, net8.0-windows) | `HostSession`, `ITurnSource`, `IInputSink`, `IHostLink`, `RelayHostLink`, `CoopHost`, `InputHookSink`, fakes (`ManualTurnSource`, `FakeInputHook`). Compiled only when `P5RCOOP_NET` is defined (see wiring). |
| `tests/P5RCoop.Tests` | net8.0 | xUnit tests. Compiles the mod's `Net/` sources directly. |
| `tests/P5RCoop.ModNet.CompileCheck` | net8.0-windows | Builds `Net/` for the mod's target framework with the mod's language settings (`EnableWindowsTargeting`, works on Linux). Ships nothing. |
| `tests/P5RCoop.FakeHost` | net8.0 exe | Stand-in for the game: real `HostSession` + typed-in turns + fake input. For trying relay + friend by hand. |

`P5RCoop.sln` at the repo root contains all of the above **except the mod project**
(`src/p5rcoop.battle/p5rcoop.battle.csproj`): it fails without the `RELOADEDIIMODS` environment
variable (`Reloaded.Checks.targets`) and is a Windows/Reloaded build. Build it as before on Windows.

## Wire format

Three layers, each with its own version so they can evolve separately:

1. **Transport framing** (TCP): `u16 length (LE) | frame bytes`. Frames over 4096 bytes close the connection.
2. **Relay frame**: `u8 relayVersion (=1) | u8 kind | body`. The relay only reads this layer.
3. **Game message** (inside `RelayData.Payload`): `u16 protocolVersion (=1, LE) | u8 type | body`.

Integers are little-endian. Strings are UTF-8 with a `u8` byte length (max 255 bytes, truncated on a
character boundary). A frame whose version header differs is refused with `ProtocolVersionException`
before the body is read; the version header must never move.

### Game messages (`P5RCoop.Protocol.MessageSerializer`, protocol v1)

| Id | Message | Direction | Body | Purpose |
|---|---|---|---|---|
| 1 | `Hello` | friend → host | `u16 protocolVersion, str name` | Join: who I am, which protocol I speak. |
| 2 | `Welcome` | host → friend | `bool accepted, str reason, u8 playerId, str hostName` | Answer to Hello. Rejected (e.g. version mismatch) → host kicks right after. |
| 3 | `SeatClaim` | friend → host | `u8 slot` | "I want to control this party slot." Slot ids are opaque bytes. |
| 4 | `SeatClaimResult` | host → friend | `u8 slot, bool accepted, str reason` | Answer to SeatClaim. |
| 5 | `SeatAssignments` | host → all | `u8 n, n × (u8 slot, str label, u8 ownerId, str ownerName)` | Full seat table, after every change. `ownerId 0` = free (host plays it). |
| 6 | `TurnChanged` | host → all | `bool hasSlot, u8 slot` | Slot whose command menu is open now, or none (no battle / enemy turn). |
| 7 | `KeyPress` | friend → host | `i32 key, u32 sequence` | One rising-edge key press. `key` = inputhook `Key` value. `sequence` starts at 1 per connection, +1 per key. |
| 8 | `Ping` | either | `u32 nonce, i64 timestampMs` | Liveness/latency probe. Friend sends every 2 s. |
| 9 | `Pong` | either | `u32 nonce, i64 timestampMs` | Echo of Ping. |
| 10 | `Leave` | either | `str reason` | Graceful leave. Host treats it like a disconnect. |

`KeyPress` is 11 bytes; `TurnChanged` is 5 bytes.

### Relay frames (`P5RCoop.Protocol.Relay.RelaySerializer`, relay v1)

| Id | Frame | Direction | Body |
|---|---|---|---|
| 1 | `CreateRoom` | host → relay | – |
| 2 | `RoomCreated` | relay → host | `str code` (6 chars, `ABCDEFGHJKMNPQRSTUVWXYZ23456789`, case-insensitive) |
| 3 | `JoinRoom` | friend → relay | `str code` |
| 4 | `JoinAccepted` | relay → friend | `u8 peerId` (1..254, unique in the room) |
| 5 | `Rejected` | relay → client | `str reason`; the relay closes the connection after it |
| 6 | `PeerJoined` | relay → host | `u8 peerId` |
| 7 | `PeerLeft` | relay → host | `u8 peerId` |
| 8 | `Data` | any | `u8 peerId, u16 len, bytes`. Host → relay: target (0 = all friends). Relay → host: sender. Friend side: ignored. |
| 9 | `Kick` | host → relay | `u8 peerId` |
| 10 | `RoomClosed` | relay → friend | `str reason` (host left) |

Relay limits: 16 friends per room, 1000 rooms, 10 s to send the first frame.

### Key codes

`P5RCoop.Protocol.KeyCodes` copies the numeric values **and names** of the `Key` enum from the
NuGet package `p5rpc.inputhook.interfaces` 1.0.0 (source: <https://github.com/AnimatedSwine37/p5rpc.inputhook>,
`p5rpc.inputhook.interfaces/Inputs.cs`, commit `1e2e04ee26520b4d98040e6b48026732333d1d6e`), including
upstream spellings such as `ASTERIX` and `APOSTRAPHE`. A test compares the table with the package's
enum by reflection, so drift fails the build. On the host a code is cast straight to `Inputs.Key`.

## Host rules (`HostSession`)

| Situation | Host keyboard | Friend keys |
|---|---|---|
| No battle / `TurnChanged(null)` | passes | dropped |
| Host's own slot (e.g. Joker: not listed in `Seats`) | passes | dropped |
| Claimable slot nobody owns | passes (host plays it) | dropped |
| Slot owned by friend F | **blocked** | F's keys injected in sequence order, duplicates dropped; other friends' keys dropped |
| F disconnects / leaves / relay lost during F's turn | passes again immediately | – (seat freed) |

Other rules: one seat per friend (claiming a new seat releases the old one); a taken seat is refused
with the owner's name; slots not in `Seats` are refused; unknown key codes are never injected;
messages before `Hello` are ignored; a bad frame or wrong version gets the peer kicked (wrong version
gets a `Welcome(accepted=false)` with a readable reason first).

### How injection meets inputhook

p5rpc.inputhook (upstream `InputHook.KeyPressed`) works like this on every game input poll:
1. if there are no host keys and nothing queued by `SendKey`, nothing happens (no intercept);
2. otherwise the list is *host keys + queued SendKey keys*, the queue is cleared;
3. `OnInputIntercept` handlers may rewrite the list; the game processes the result.

So `HostSession` calls `SendKey` for each accepted friend key (that is what wakes the hook when the
host is idle) and remembers it as *pending*. In the intercept (`FilterInput`) it finds the pending
keys at the tail of the list, removes them, and then:
- friend turn: clears the host keys and puts back the injected keys that were sent **during this turn**;
- otherwise: leaves the host keys; injected keys that arrived after the turn ended are discarded.

`FakeInputHook` reproduces steps 1–3 exactly, which is what the tests run against.

### Known limitations

- **Thread safety in inputhook**: `SendKey` appends to a plain `List` that the game thread reads and
  clears inside the hook. `HostSession` calls `SendKey` from the network thread, so a key arriving
  exactly during a poll could race. Rare (keys are human-speed) but possible. If it shows up in
  practice: call `SendKey` from the game thread instead (e.g. queue keys and flush them from the
  turn-source hook, which runs on the game thread), or ask upstream for a lock.
- inputhook is keyboard-only and rising-edge only: no held keys, no controller.
- TCP head-of-line blocking is irrelevant at these rates; the reorder buffer only matters for a future
  unreliable transport (gives up on a gap after 16 pending keys).
- No authentication: the join code is the only secret. Run your own relay.

## Run it locally (no game)

```sh
dotnet build P5RCoop.sln
dotnet test P5RCoop.sln

# terminal 1: relay
dotnet run --project src/P5RCoop.Relay -- --port 7777

# terminal 2: fake host (prints a join code; type "t 1" to give slot 1 the turn, "t 0" for the host,
# "t -" for no battle, "h ENTER" for the host pressing ENTER, "s" for status)
dotnet run --project tests/P5RCoop.FakeHost -- --relay 127.0.0.1:7777 --seats "1=Ryuji,2=Ann"

# terminal 3: a friend (interactive: real key presses; or pipe key names, one per line)
dotnet run --project src/P5RCoop.Friend -- --relay 127.0.0.1:7777 --code <CODE> --name Friend1 --slot Ryuji
```

The friend sees seat and turn changes; key presses outside their turn print
`Dropped ENTER: it is Joker's turn`-style messages. The fake host prints what the "game" processed.

## Wiring the mod

`src/p5rcoop.battle/Net/*.cs` are wrapped in `#if P5RCOOP_NET`, so merging this branch does **not**
change the mod build: the files compile to nothing until the steps below are done. All four steps
were applied to a scratch copy of the mod and built for net8.0-windows on Linux
(`dotnet build p5rcoop.battle.csproj -p:EnableWindowsTargeting=true`, 0 warnings, 0 errors); the
output folder then contains `P5RCoop.Protocol.dll` and `p5rpc.inputhook.interfaces.dll`.

### 1. `src/p5rcoop.battle/p5rcoop.battle.csproj`

```xml
  <PropertyGroup>
    ...
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Turns on the co-op networking code in Net/ (see docs/NETWORKING.md). -->
    <DefineConstants>$(DefineConstants);P5RCOOP_NET</DefineConstants>
    ...
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.ILLink.Tasks" Version="8.0.21" />
    <PackageReference Include="Reloaded.Mod.Interfaces" Version="2.4.0" ExcludeAssets="runtime" />
    <PackageReference Include="Reloaded.SharedLib.Hooks" Version="1.9.0" />
    <PackageReference Include="p5rpc.inputhook.interfaces" Version="1.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\P5RCoop.Protocol\P5RCoop.Protocol.csproj" />
  </ItemGroup>
```

### 2. `src/p5rcoop.battle/ModConfig.json`

```json
  "ModDependencies": [ "reloaded.sharedlib.hooks", "p5rpc.inputhook" ],
```

and install the **p5rpc.inputhook** mod in Reloaded-II (it is a library mod; Reloaded resolves the
dependency from its index or it can be downloaded from the upstream releases).

### 3. `src/p5rcoop.battle/Config.cs` (add to `Config`)

```csharp
    [Category("Co-op")]
    [DisplayName("Enable co-op")]
    [Description("Connect to the relay and let friends control party members in battle.")]
    [DefaultValue(false)]
    public bool CoopEnabled { get; set; } = false;

    [Category("Co-op")]
    [DisplayName("Relay address")]
    [Description("host:port of the P5RCoop.Relay server everyone connects to.")]
    [DefaultValue("127.0.0.1:7777")]
    public string RelayAddress { get; set; } = "127.0.0.1:7777";

    [Category("Co-op")]
    [DisplayName("Host name")]
    [Description("Name friends see.")]
    [DefaultValue("Host")]
    public string HostName { get; set; } = "Host";

    [Category("Co-op")]
    [DisplayName("Seats")]
    [Description("Party slots friends may claim, as slot=label pairs. Slot ids must match the turn source.")]
    [DefaultValue("1=Ryuji,2=Ann")]
    public string Seats { get; set; } = "1=Ryuji,2=Ann";
```

### 4. `src/p5rcoop.battle/Mod.cs`

```csharp
using p5rcoop.battle.Net;
using p5rcoop.battle.Net.Fakes;
using p5rpc.inputhook.interfaces;
using P5RCoop.Protocol.Transport;

    // fields
    // Co-op networking (docs/NETWORKING.md). Replace ManualTurnSource with the real battle hook.
    private readonly ManualTurnSource _turns = new();
    private InputHookSink? _inputSink;
    private CoopHost? _coop;

    // end of the constructor
        if (_configuration.CoopEnabled)
            StartCoop();

    private void StartCoop()
    {
        var inputController = _modLoader.GetController<IInputHook>();
        if (inputController == null || !inputController.TryGetTarget(out var inputHook))
        {
            _logger.WriteLine($"[{_modConfig.ModId}] p5rpc.inputhook not available, co-op disabled");
            return;
        }
        _inputSink = new InputHookSink(inputHook);
        var options = new HostSessionOptions
        {
            HostName = _configuration.HostName,
            Seats = HostSessionOptions.ParseSeats(_configuration.Seats),
            Log = line => _logger.WriteLine($"[{_modConfig.ModId}] {line}"),
        };
        _ = Task.Run(async () =>
        {
            try
            {
                _coop = await CoopHost.StartAsync(new TcpTransport(), _configuration.RelayAddress, options, _turns, _inputSink);
                _logger.WriteLine($"[{_modConfig.ModId}] Co-op room open. Join code: {_coop.Code}");
                await _coop.Completion;
                _logger.WriteLine($"[{_modConfig.ModId}] Relay connection closed; host has full control");
            }
            catch (Exception e)
            {
                _logger.WriteLine($"[{_modConfig.ModId}] Co-op failed to start: {e.Message}");
            }
        });
    }
```

### 5. The turn source (needs the reverse-engineering result)

Implement `ITurnSource` over the game hook that knows whose command menu is open: set `CurrentSlot`
to the acting party member's slot id when their menu opens, to `null` outside battle and on enemy
turns, and raise `TurnChanged` on every change. Pick slot ids that match `Seats` (e.g. the game's own
party member ids). Replace `ManualTurnSource` in Mod.cs with it.

Until then `ManualTurnSource` can be driven by debug keys for in-game testing. Subscribe this
**before** `new InputHookSink(inputHook)`: intercept handlers run in subscription order, and the
session's handler removes host keys during a friend's turn (`OnInput` runs after all intercepts, so it
would never see them):

```csharp
inputHook.OnInputIntercept += keys =>
{
    if (keys.Contains(Inputs.Key.F9)) _turns.SetTurn(null);
    if (keys.Contains(Inputs.Key.F10)) _turns.SetTurn(0); // host
    if (keys.Contains(Inputs.Key.F11)) _turns.SetTurn(1); // e.g. Ryuji
    if (keys.Contains(Inputs.Key.F12)) _turns.SetTurn(2); // e.g. Ann
};
```

## Licensing

p5rpc.inputhook (the Reloaded mod that does the hooking) is **GPL-3.0**; its interfaces package is
LGPL-3.0-or-later. A mod built on it should be released as GPL-3.0. This repo has no LICENSE file
yet; adding one is the owner's call.

## Tests

`dotnet test P5RCoop.sln` (xUnit, Linux, in-memory transport plus real loopback TCP where noted):

- serializer round-trips for every message and relay frame, compact sizes, truncated/trailing/unknown
  frames, UTF-8 truncation, `KeyCodes` vs the real inputhook `Key` enum;
- version mismatch: frame header, Hello field, relay version, friend app error text;
- seat claims: taken, unknown slot, re-claim, move, friend-to-friend conflict over the relay;
- turn handoff broadcast; host turn / no battle / unclaimed slot pass host keys and drop friend keys;
- friend turn blocks host keys, injects friend keys even with no host input, drops other friends' keys;
- ordering and duplicate suppression (out-of-order + repeated sequence numbers), reorder gap skip;
- stale keys from a finished turn never reach the next turn;
- disconnect, Leave and relay loss mid-turn fall back to the host;
- relay forwarding with a host and two friends (memory and TCP), targeted vs broadcast, opaque
  payloads, kick, leave, room close, unknown code, handshake timeout;
- a scripted battle Joker (host) → Ryuji (friend 1) → Ann (friend 2) → host over relay + friend app
  (memory and TCP).
