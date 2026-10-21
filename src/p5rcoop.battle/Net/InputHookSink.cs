// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
// Needs the p5rpc.inputhook.interfaces package (LGPL-3.0-or-later) and the p5rpc.inputhook mod (GPL-3.0).
#if P5RCOOP_NET
using System.Collections.Generic;
using p5rpc.inputhook.interfaces;
using static p5rpc.inputhook.interfaces.Inputs;

namespace p5rcoop.battle.Net;

/// <summary>
/// <see cref="IInputSink"/> backed by p5rpc.inputhook. Get the hook in Mod.cs with
/// <c>_modLoader.GetController&lt;IInputHook&gt;()</c>.
/// Known limitation: inputhook's SendKey queue is a plain List touched by the game thread, and
/// <see cref="HostSession"/> calls SendKey from the network thread. See docs/NETWORKING.md.
/// </summary>
public sealed class InputHookSink : IInputSink, System.IDisposable
{
    private readonly IInputHook _hook;
    private readonly List<int> _scratch = new();

    public InputHookSink(IInputHook hook)
    {
        _hook = hook;
        _hook.OnInputIntercept += OnInputIntercept;
    }

    public event InputFilter? Intercept;

    public void SendKey(int keyCode) => _hook.SendKey((Key)keyCode);

    private void OnInputIntercept(List<Key> keys)
    {
        var filter = Intercept;
        if (filter is null)
            return;
        _scratch.Clear();
        foreach (var key in keys)
            _scratch.Add((int)key);
        filter(_scratch);
        keys.Clear();
        foreach (var code in _scratch)
            keys.Add((Key)code);
    }

    public void Dispose() => _hook.OnInputIntercept -= OnInputIntercept;
}
#endif
