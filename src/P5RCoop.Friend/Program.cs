using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using P5RCoop.Friend;
using P5RCoop.Protocol;
using P5RCoop.Protocol.Transport;

// Usage: P5RCoop.Friend --relay host:port --code ABC123 --name Me [--slot 2|Ryuji]
string? relay = null, code = null, name = null, slot = null;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i])
    {
        case "--relay" or "-r": relay = Next(); break;
        case "--code" or "-c": code = Next(); break;
        case "--name" or "-n": name = Next(); break;
        case "--slot" or "-s": slot = Next(); break;
        case "--help" or "-h":
            Console.WriteLine("Usage: P5RCoop.Friend --relay host:port --code JOINCODE --name NAME [--slot NUMBER|LABEL]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}");
            return 2;
    }
}

relay ??= Prompt("Relay address (host:port)", "127.0.0.1:7777");
code ??= Prompt("Join code", null);
name ??= Prompt("Your name", Environment.UserName);

var ui = new ConsoleUi();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

FriendClient client;
try
{
    var connection = await new TcpTransport().ConnectAsync(relay, cts.Token);
    client = await FriendClient.JoinAsync(connection, code, name, ui, ct: cts.Token);
}
catch (Exception e) when (e is JoinFailedException or System.Net.Sockets.SocketException or FormatException)
{
    ui.Warn($"Could not join: {e.Message}");
    return 1;
}

using (client)
{
    ui.Info($"Joined {client.HostName}'s game as player {client.PlayerId} (protocol v{ProtocolInfo.Version}).");
    ui.Info("Seats: " + string.Join(", ", client.Seats.Select(s => $"[{s.Slot}] {s.Label}{(s.IsFree ? "" : $" ({s.OwnerName})")}")));

    while (client.MySlot is null && !cts.IsCancellationRequested)
    {
        slot ??= Prompt("Which seat do you want (number or name)", null);
        var seat = client.FindSeat(slot);
        slot = null;
        if (seat is null)
        {
            ui.Warn("No such seat.");
            continue;
        }
        SeatClaimResult result;
        try
        {
            result = await client.ClaimSeatAsync(seat.Slot, cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or JoinFailedException)
        {
            return 1;
        }
        if (result.Accepted)
            ui.Info($"You control {seat.Label}.");
    }

    IKeySource source = Console.IsInputRedirected ? new LineKeySource(Console.In, ui) : new ConsoleKeySource(ui);
    ui.Info(Console.IsInputRedirected
        ? "Reading key names (ENTER, DOWN, ...) from stdin, one per line."
        : "Key mode: your key presses go to the game during your character's turn. Ctrl+C quits.");
    var keys = client.RunKeysAsync(source, cts.Token);
    await Task.WhenAny(keys, client.Completion, Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => { }));
    if (!client.Completion.IsCompleted)
        await client.LeaveAsync("quit");
}
return 0;

static string Prompt(string label, string? fallback)
{
    while (true)
    {
        Console.Write(fallback is null ? $"{label}: " : $"{label} [{fallback}]: ");
        var line = Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(line))
            return line;
        if (fallback is not null)
            return fallback;
    }
}

internal sealed class ConsoleUi : IFriendUi
{
    private readonly object _gate = new();

    public void Info(string line) => Write(line, ConsoleColor.Gray);

    public void Warn(string line) => Write(line, ConsoleColor.Yellow);

    private void Write(string line, ConsoleColor color)
    {
        lock (_gate)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            Console.ForegroundColor = old;
        }
    }
}
