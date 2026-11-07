# Building A-19J

The released source is a single C# BepInEx plugin. The build script references assemblies from your own game installation; it does not download or bundle game code or dependencies.

## Prerequisites

- Windows PowerShell 7.
- Nuclear Option 0.34.6, with its Mono managed assemblies.
- BepInEx 5 Mono x64 in the game folder.
- Blueprinter 2.1.1 at `BepInEx/plugins/com.nikkorap.blueprinter/Blueprinter_2.1.1.dll`.
- The compiler and .NET runtime included in Unity Editor **2022.3.62f3**. The tested compiler reports `4.3.1-3.22526.13`.

## Compile

From this repository's root, run:

```powershell
pwsh -File ./build.ps1
```

If your paths differ:

```powershell
pwsh -File ./build.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Nuclear Option' -EditorDataPath 'D:\Unity\2022.3.62f3\Editor\Data'
```

The output is `build/A19J.dll`. The compiler runs with optimisation and deterministic output enabled. The source is supplied as published; no generated source or private project files are needed.

Exact binary reproduction depends on the same compiler and reference assemblies. `test-report.json` records the tested game's `Assembly-CSharp.dll` SHA-256, and `code-audit.json` records the released DLL and source SHA-256 values. A different compiler or different game assemblies can produce a different binary without a source change.

## Review

```powershell
pwsh -File ./audit.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Nuclear Option'
```

This performs a limited static review of assembly references and direct method calls, including nested compiler-generated methods. It flags direct filesystem/network/process/registry/native-loader calls. It is a review aid, not a universal safety guarantee.

## Developer validation

The released DLL includes explicit environment-controlled test routines. With `A19J_VALIDATE=1`, it validates separate registration and reference ownership, then exits the test game process. With `A19J_VALIDATE_SPAWN=1` as well, it opens the stock **offline encyclopedia**, spawns A-19J and performs a restrained 20-second engine run-up before exiting.

These tests are only intended for an isolated test launch. They do not replace normal flight or multiplayer testing. Do not set these environment switches for ordinary play. The validation branch's process-exit behaviour is present in the public source; no private code is injected into the release.

In v0.1.2, validation also exercises every current online socket mode plus an unknown mode, removal/re-registration, stock aircraft passthrough and preservation of later definitions. The spawn validation sends an online-mode probe through the patched native spawner inside the isolated offline test and restores offline mode; it creates no online connection. These probes do not constitute an online multiplayer session test. Normal operation accepts only confirmed offline local hosts.
