# Persona 5 Royal co-op

**Battle co-op for Persona 5 Royal (PC).** The host plays the game; each friend takes control of
one party member and picks that character's commands in battle (attack, skill, Persona, item, guard).

Work in progress, built with the [Co-op Anything](https://github.com/cranebeaker/coop-anything) playbook.
This repo contains no game files.

## Status

| | |
|---|---|
| Game version | Steam, P5R.exe 1.0.4.0 (build 15515071) |
| Stage | Research done; toolchain setup next |
| QA level | none (nothing tested yet) |

## Plan

1. Setup: Reloaded-II mod loader, .NET SDK, isolated test copy of the game, a do-nothing test mod.
2. Find the hook: where the battle menu asks what a party member does.
3. Prototype: a second window on the same PC picks one party member's commands.
4. Networking: friends connect over Steam relay (no port forwarding), then packaging and automated tests.

## Related work

- [p5r.multiplayer](https://github.com/morgana-x/p5r.multiplayer) syncs player characters walking
  around the overworld. It does not cover battles.

Research log: [NOTES.md](NOTES.md).
