using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using P5RCoop.Protocol;

namespace P5RCoop.Friend;

/// <summary>
/// Reads keys from the console window with <see cref="Console.ReadKey(bool)"/>. Console input has no
/// key-up events, so every ReadKey is one rising edge, which matches what p5rpc.inputhook injects.
/// </summary>
public sealed class ConsoleKeySource : IKeySource
{
    private readonly IFriendUi _ui;

    public ConsoleKeySource(IFriendUi ui)
    {
        _ui = ui;
    }

    public async IAsyncEnumerable<int> ReadKeysAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keys = Channel.CreateUnbounded<int>();
        var reader = new Thread(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var info = Console.ReadKey(intercept: true);
                if (ConsoleKeyMap.TryMap(info.Key, out var code))
                    keys.Writer.TryWrite(code);
                else
                    _ui.Warn($"{info.Key} has no P5R key mapping, ignored");
            }
        }) { IsBackground = true, Name = "console-keys" };
        reader.Start();

        while (await keys.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (keys.Reader.TryRead(out var code))
                yield return code;
        }
    }
}

/// <summary>Maps <see cref="ConsoleKey"/> to inputhook key codes.</summary>
public static class ConsoleKeyMap
{
    private static readonly Dictionary<ConsoleKey, int> s_map = Build();

    public static IReadOnlyDictionary<ConsoleKey, int> Map => s_map;

    public static bool TryMap(ConsoleKey key, out int code) => s_map.TryGetValue(key, out code);

    private static Dictionary<ConsoleKey, int> Build()
    {
        var map = new Dictionary<ConsoleKey, int>();
        for (var i = 0; i < 26; i++)
            map[ConsoleKey.A + i] = KeyCodes.A + i;
        // inputhook orders digits 1..9 then 0.
        for (var i = 1; i <= 9; i++)
        {
            map[ConsoleKey.D0 + i] = KeyCodes._1 + (i - 1);
            map[ConsoleKey.NumPad0 + i] = KeyCodes.NUM_1 + (i - 1);
        }
        map[ConsoleKey.D0] = KeyCodes._0;
        map[ConsoleKey.NumPad0] = KeyCodes.NUM_0;
        for (var i = 0; i < 12; i++)
            map[ConsoleKey.F1 + i] = KeyCodes.F1 + i;

        map[ConsoleKey.Enter] = KeyCodes.ENTER;
        map[ConsoleKey.Escape] = KeyCodes.ESCAPE;
        map[ConsoleKey.Backspace] = KeyCodes.BACKSPACE;
        map[ConsoleKey.Tab] = KeyCodes.TAB;
        map[ConsoleKey.Spacebar] = KeyCodes.SPACE;
        map[ConsoleKey.OemMinus] = KeyCodes.MINUS;
        map[ConsoleKey.OemPlus] = KeyCodes.EQUALS;
        map[ConsoleKey.OemComma] = KeyCodes.COMMA;
        map[ConsoleKey.OemPeriod] = KeyCodes.DOT;
        map[ConsoleKey.Oem1] = KeyCodes.SEMICOLON;
        map[ConsoleKey.Oem2] = KeyCodes.FORWARD_SLASH;
        map[ConsoleKey.Oem3] = KeyCodes.GRAVE;
        map[ConsoleKey.Oem4] = KeyCodes.L_BRACKET;
        map[ConsoleKey.Oem5] = KeyCodes.BACKSLASH;
        map[ConsoleKey.Oem6] = KeyCodes.R_BRACKET;
        map[ConsoleKey.Oem7] = KeyCodes.APOSTRAPHE;
        map[ConsoleKey.UpArrow] = KeyCodes.UP;
        map[ConsoleKey.DownArrow] = KeyCodes.DOWN;
        map[ConsoleKey.LeftArrow] = KeyCodes.LEFT;
        map[ConsoleKey.RightArrow] = KeyCodes.RIGHT;
        map[ConsoleKey.Insert] = KeyCodes.INSERT;
        map[ConsoleKey.Delete] = KeyCodes.DEL;
        map[ConsoleKey.Home] = KeyCodes.HOME;
        map[ConsoleKey.End] = KeyCodes.END;
        map[ConsoleKey.PageUp] = KeyCodes.PAGE_UP;
        map[ConsoleKey.PageDown] = KeyCodes.PAGE_DOWN;
        map[ConsoleKey.Multiply] = KeyCodes.ASTERIX;
        map[ConsoleKey.Add] = KeyCodes.PLUS;
        map[ConsoleKey.Subtract] = KeyCodes.NUM_MINUS;
        map[ConsoleKey.Divide] = KeyCodes.NUM_FORWARD_SLASH;
        map[ConsoleKey.Decimal] = KeyCodes.NUM_DOT;
        return map;
    }
}

/// <summary>
/// Key source for redirected stdin (scripts, pipes): one key name per line, using the inputhook names
/// from <see cref="KeyCodes"/> (e.g. ENTER, DOWN, W). Unknown names are reported and skipped.
/// </summary>
public sealed class LineKeySource : IKeySource
{
    private readonly System.IO.TextReader _input;
    private readonly IFriendUi _ui;

    public LineKeySource(System.IO.TextReader input, IFriendUi ui)
    {
        _input = input;
        _ui = ui;
    }

    public async IAsyncEnumerable<int> ReadKeysAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var name = line.Trim();
            if (name.Length == 0)
                continue;
            var match = KeyCodes.Names.FirstOrDefault(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase));
            if (match.Value is null)
                _ui.Warn($"Unknown key name {name}");
            else
                yield return match.Key;
        }
    }
}
