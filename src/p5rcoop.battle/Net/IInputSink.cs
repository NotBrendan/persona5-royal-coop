// Compiled only when the mod defines P5RCOOP_NET (see docs/NETWORKING.md, "Wiring the mod").
#if P5RCOOP_NET
using System.Collections.Generic;

namespace p5rcoop.battle.Net;

/// <summary>Edits the list of key codes the game is about to process. Runs on the game thread.</summary>
public delegate void InputFilter(List<int> keys);

/// <summary>
/// The game's keyboard input, modelled on p5rpc.inputhook's IInputHook: <see cref="SendKey"/> queues a
/// key that is appended to the next poll, then <see cref="Intercept"/> sees host keys followed by queued
/// keys and may rewrite the list. Key codes are <c>P5RCoop.Protocol.KeyCodes</c> values (= inputhook Key).
/// Real implementation: <see cref="InputHookSink"/>; test double: <see cref="Fakes.FakeInputHook"/>.
/// </summary>
public interface IInputSink
{
    void SendKey(int keyCode);

    event InputFilter? Intercept;
}
#endif
