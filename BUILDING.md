# Building A-19J

The DLL is built from A19JPlugin.cs, A19JNetworking.cs and A19JMultiplayerValidation.cs. All three files must remain beside build.ps1. It references assemblies from your own installed Nuclear Option 0.34.6, BepInEx 5 and Blueprinter 2.1.1; game code and dependency DLLs are not bundled.

Requires PowerShell 7 and Unity Editor 2022.3.62f3's bundled Roslyn compiler (tested version 4.3.1-3.22526.13).

```powershell
pwsh -File ./build.ps1
# For other installation locations:
pwsh -File ./build.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Nuclear Option' -EditorDataPath 'D:\Unity\2022.3.62f3\Editor\Data'
pwsh -File ./audit.ps1
```

The output is build/A19J.dll. Compilation uses deterministic output and optimization. Exact reproduction requires the same compiler and reference assemblies. The test report records the game assembly hash; code-audit.json records each source hash and the DLL hash.

## Networking

The aircraft uses the native A-19 network behaviours and ownership/physics path, a distinct deterministic prefab hash, and an index appended after Blueprinter completes loading. Registration persists across offline/online transitions. Inventory writes require an active server, an authoritative FactionHQ and a writable supply dictionary; clients consume native replicated state.

A fixed-size pre-authentication compatibility message carries a protocol value, definition count and deterministic catalog checksum. Its reader/writer delegates are explicitly registered with Mirage. The native authentication result is awaited and preserved; compatibility is an additional check before native authentication completes. The checksum is a compatibility signal, not a security/authentication primitive.

## Audit scope

audit.ps1 examines direct calls including nested async/compiler-generated methods. It flags filesystem, general network, process, registry and native-loader calls. Two endpoint-construction calls are explicitly allowlisted only in ValidationLoopbackBind. This method is installed as a Harmony hook only under A19J_VALIDATE=1 and builds an IPAddress.IPv6Loopback endpoint. The test also forces managed UDP and rejects Steam transport configuration. No general socket/request/download API is used by the plugin. The audit is a review aid, not a universal safety guarantee.

## Disposable validation

Normal play requires none of the following environment switches.

- A19J_VALIDATE=1: registration, prefab/reference ownership and compatibility checks; quits the test process.
- Also A19J_VALIDATE_SPAWN=1: native offline encyclopedia spawn, isolated inventory/factory/queue fixtures, and restrained engine run-up.
- Also A19J_VALIDATE_MULTIPLAYER=host or client: separate processes in a native Free Flight mission. The host's actual socket bind endpoint is forced to [::1]:28791. Both processes suppress native dedicated auto-start. No Steam lobby is created or joined by this test.
- host-negative with a mismatch or missing client: verifies rejection before remote mission/player spawn. The mismatch/missing roles alter or suppress the test compatibility offer only when A19J_VALIDATE=1.

The multiplayer test receives the native mission/map, faction dictionary and aircraft prefab, checks both engines and their native state, and checks native despawning. It does not simulate a two-PC Steam session or certify flight balance. The isolated inventory fixture uses a validation-only reverse patch to the original mission-load body, with temporary price/rank/owned-airframe values restored afterward. Other installed mod DLLs are compared against a fresh baseline.
