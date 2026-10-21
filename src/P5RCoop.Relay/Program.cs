using System;
using System.Threading;
using P5RCoop.Protocol.Transport;
using P5RCoop.Relay;

// Usage: P5RCoop.Relay [port] [--bind address]
var port = 7777;
var bind = "0.0.0.0";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" or "-p" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--bind" when i + 1 < args.Length:
            bind = args[++i];
            break;
        case "--help" or "-h":
            Console.WriteLine("Usage: P5RCoop.Relay [--port 7777] [--bind 0.0.0.0]");
            return 0;
        default:
            if (int.TryParse(args[i], out var p))
                port = p;
            else
            {
                Console.Error.WriteLine($"Unknown argument {args[i]}");
                return 2;
            }
            break;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var listener = new TcpTransport().Listen($"{bind}:{port}");
var server = new RelayServer(listener, line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"));
await server.RunAsync(cts.Token);
return 0;
