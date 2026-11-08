# A-19J — Jet-Powered Brawler

The A-19 Brawler trades its propellers for twin conventional jets while keeping its familiar cockpit, armour and weapons. A-19J is a separate aircraft; the original A-19 stays available.

**Development prerelease · v0.2.0 · Nuclear Option 0.34.6 · Single-player and multiplayer**

[Download A19J.dll](https://github.com/markus21alpha-dot/A19J/releases/download/v0.2.0/A19J.dll) · [Project page](https://markus21alpha-dot.github.io/A19J/) · [Report an issue](https://github.com/markus21alpha-dot/A19J/issues)

![A-19J model preview](A19J-rear.png)

*Model render. In-game appearance and lighting vary.*

## AI Disclaimer

AI assistance was used to write the mod code, create the custom procedural exhaust housings, rims and interiors, and help assemble, compile, test and document the mod. The base aircraft and reused game assets come from Nuclear Option. Extracted game assets and dependency binaries are not distributed.

## Install and play with a friend

1. Close Nuclear Option. Install BepInEx 5 Mono x64 and [Blueprinter 2.1.1](https://github.com/nikkorap/NOBlueprinter-Releases/releases/tag/2.1.1).
2. Both the host and every joining player must install **A19J v0.2.0** and the same aircraft/weapon mods. Use the NOMM catalog entry when it offers v0.2.0, or download the DLL from this repository's [v0.2.0 release](https://github.com/markus21alpha-dot/A19J/releases/tag/v0.2.0).
3. Replace `Nuclear Option/BepInEx/plugins/A19J/A19J.dll`. Keep only one copy of A19J.dll.
4. Restart the game on both PCs. Host a private multiplayer mission through the normal game menu and invite your friend. A-19J is added to hangars that offer A-19, subject to the mission's faction stock, restrictions, rank and airbase rules.

Version 0.2.0 removes the earlier offline-only restriction. Host and client register the same deterministic aircraft prefab and retain its definition index across session transitions. The original aircraft physics, ownership, controls, damage, weapons and native network replication remain in use.

A compatibility exchange checks the A-19J version and ordered network-definition catalog before the host sends mission data. Missing or mismatched installs are rejected with an update message. It preserves the game's existing Steam/build/password/ban authentication. It does not download mods to another player's PC. Updating only the host is insufficient.

A-19J's original NOMM listing was [accepted in #500](https://github.com/KopterBuzz/NOMNOM/pull/500). The v0.2.0 catalog update is tracked in [#511](https://github.com/KopterBuzz/NOMNOM/pull/511). Check the version shown by NOMM; a pending registry update does not make the new DLL available there yet.

## Faction stock

Existing missions without an explicit A19J supply entry offer A-19J as a conversion of the faction's A-19 airframes. Both show the same real stock count. Requesting either consumes one shared airframe; native factory production replenishes the pool, including queued A-19J requests. An authored A19J entry, including zero, remains independent.

The server owns stock changes. Remote clients receive the native replicated supply dictionary. Shared stock inherits A-19 faction restrictions. Restart the mission after updating.

## Aircraft

- Two conventional 48 kN jets with native spool, fuel, sound and infrared behaviour.
- Complete tapered exhaust housings, metal lips and recessed interiors at close and distant LODs.
- Original A-19 cockpit, weapons, armour, fuel layout and 33 airframe damage parts.
- No afterburners or thrust vectoring.

Native thrust falls off above 235 m/s and reaches zero at 282 m/s. The displayed 920 km/h maximum is a design target; flight handling and combat balance remain under development.

## Validation and limits

The release is checked with separate local host/client processes in a native Free Flight mission using a loopback-only UDP endpoint. The checks cover authentication compatibility, late-join aircraft identity, two remote jets, faction stock replication, engine operation and despawning. Negative cases cover a missing compatibility offer and a mismatched version. Separate checks cover stock consumption, factories, queued replacements and offline spawning.

The test uses two processes on one PC. A separate two-PC Steam session, complete flight handling and combat balance still need player testing. This is a development prerelease, not a claim that every mod combination or multiplayer scenario has been tested. See [test-report.json](test-report.json), [validation-evidence.txt](validation-evidence.txt) and [code-audit.json](code-audit.json).

## Source and developer tests

Complete matching source: [A19JPlugin.cs](A19JPlugin.cs), [A19JNetworking.cs](A19JNetworking.cs) and [A19JMultiplayerValidation.cs](A19JMultiplayerValidation.cs). [BUILDING.md](BUILDING.md) explains deterministic compilation and the limited direct-call audit. No obfuscation, automatic downloads, credential handling, process launches, registry changes or direct file writes are added by the plugin.

Explicit developer environment switches enable disposable tests: `A19J_VALIDATE=1`, `A19J_VALIDATE_SPAWN=1` and `A19J_VALIDATE_MULTIPLAYER`. Ordinary launches do not use them. Test mode suppresses dedicated-server auto-start, rejects Steam transports, binds its actual UDP test endpoint to IPv6 loopback, runs checks and quits its own process. The endpoint-construction code is declared in the audit. Do not enable these switches for ordinary play.

## Credits and licence

Original mod code and procedural exhaust geometry: **markus21alpha-dot**, developed with AI assistance. [MIT licence](LICENSE) covers original mod content only. Nuclear Option and its assets belong to Shockfront Studios; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). This is an unofficial community mod.
