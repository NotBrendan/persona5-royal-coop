using p5rcoop.battle.Net;
using p5rcoop.battle.Net.Fakes;
using P5RCoop.Protocol;
using P5RCoop.Protocol.Transport;

// A fake game host for trying the relay and friend app without P5R.
// Usage: P5RCoop.FakeHost [--relay 127.0.0.1:7777] [--seats "1=Ryuji,2=Ann"]
var relay = "127.0.0.1:7777";
var seats = "1=Ryuji,2=Ann";
for (var i = 0; i + 1 < args.Length; i += 2)
{
    if (args[i] == "--relay") relay = args[i + 1];
    else if (args[i] == "--seats") seats = args[i + 1];
}

var turns = new ManualTurnSource();
var game = new FakeInputHook();
using var host = await CoopHost.StartAsync(new TcpTransport(), relay, new HostSessionOptions
{
    HostName = "FakeHost",
    Seats = HostSessionOptions.ParseSeats(seats),
    Log = line => Console.WriteLine($"[host] {line}"),
}, turns, game);

Console.WriteLine($"Join code: {host.Code}");
Console.WriteLine("Commands: t <slot> = that slot's turn, t - = no battle, h <KEY> = host presses KEY, s = status, q = quit");

// The "game" polls input every 50 ms and prints whatever it ends up processing.
using var timer = new Timer(_ =>
{
    var seen = game.Poll();
    if (seen.Count > 0)
        Console.WriteLine($"[game] processed: {string.Join(" ", seen.Select(KeyCodes.NameOf))}");
}, null, 0, 50);

while (Console.ReadLine() is { } line)
{
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0)
        continue;
    switch (parts[0])
    {
        case "t" when parts.Length > 1:
            turns.SetTurn(parts[1] == "-" ? null : byte.Parse(parts[1]));
            break;
        case "h" when parts.Length > 1:
            var key = KeyCodes.Names.FirstOrDefault(kv => string.Equals(kv.Value, parts[1], StringComparison.OrdinalIgnoreCase));
            if (key.Value is null)
            {
                Console.WriteLine($"Unknown key {parts[1]}; use inputhook names like ENTER, UP, W");
                break;
            }
            var seen = game.Poll(key.Key);
            Console.WriteLine($"[game] host pressed {parts[1]}, processed: {(seen.Count == 0 ? "(nothing, blocked)" : string.Join(" ", seen.Select(KeyCodes.NameOf)))}");
            break;
        case "s":
            var s = host.Session;
            Console.WriteLine($"turn={s.CurrentSlot?.ToString() ?? "-"} acting={s.ActingPeer?.ToString() ?? "host"} injected={s.InjectedKeys} dropped={s.DroppedOffTurnKeys} dup={s.DuplicateKeys} blocked={s.BlockedHostKeys}");
            foreach (var seat in s.Seats)
                Console.WriteLine($"  [{seat.Slot}] {seat.Label}: {(seat.IsFree ? "host" : seat.OwnerName)}");
            break;
        case "q":
            return;
    }
}
