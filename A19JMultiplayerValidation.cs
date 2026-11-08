using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Mirage;
using Mirage.Serialization;
using Mirage.SocketLayer;
using Mirage.Sockets.Udp;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace A19JConversion
{
    public sealed partial class A19JPlugin
    {
        // Registered only by the explicit developer test coroutine. None of these
        // messages or localhost connections are used in ordinary gameplay.
        private struct ValidationMessage
        {
            public uint Stage;
            public uint NetId;
            public uint Engines;
            public uint RPM;
            public uint Stock;
        }
        private static INetworkPlayer validationPeer;
        private static uint validationStage;
        private static ValidationMessage validationSnapshot;

        private void InstallValidationIsolation()
        {
            // The native UdpSocketFactory.Address affects connect endpoints, but
            // GetBindEndPoint ignores it and normally binds IPv6Any. Override the
            // actual endpoint in this explicit test process, not just Address.
            harmony.Patch(AccessTools.Method(typeof(UdpSocketFactory), "GetBindEndPoint"),
                postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(ValidationLoopbackBind)));
            harmony.Patch(AccessTools.Method(typeof(NetworkManagerNuclearOption), "ConfigureNetwork"),
                prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(ValidationTransportOnly)));
        }

        private static void ValidationTransportOnly(NetworkManagerNuclearOption __instance, SocketType __0)
        {
            if (__0 != SocketType.Offline && __0 != SocketType.UDP)
                throw new InvalidOperationException("Test isolation forbids Steam/public transport startup.");
            Get<UdpSocketFactory>(__instance, "udpTransport").SocketLib = SocketLib.Managed;
        }

        private static void ValidationLoopbackBind(UdpSocketFactory __instance, ref IBindEndPoint __result)
        {
            if (__instance.SocketLib != SocketLib.Managed) throw new InvalidOperationException("Test socket must use managed loopback binding.");
            __result = new UdpConnectionHandle(new IPEndPoint(IPAddress.IPv6Loopback, __instance.Port));
            Debug.Log("A19J TEST SOCKET BIND: [::1]:" + __instance.Port);
        }

        private static async UniTask WaitForValidation(Func<bool> condition, float seconds, string failure)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline) throw new InvalidOperationException(failure);
                await UniTask.Delay(50, ignoreTimeScale: true);
            }
        }

        private async UniTask MultiplayerValidation(string role)
        {
            if (Environment.GetEnvironmentVariable("A19J_VALIDATE") != "1") throw new InvalidOperationException("Explicit test mode required.");
            Writer<ValidationMessage>.Write = (w, m) =>
            { w.WriteUInt32(m.Stage); w.WriteUInt32(m.NetId); w.WriteUInt32(m.Engines); w.WriteUInt32(m.RPM); w.WriteUInt32(m.Stock); };
            Reader<ValidationMessage>.Read = r => new ValidationMessage
            { Stage = r.ReadUInt32(), NetId = r.ReadUInt32(), Engines = r.ReadUInt32(), RPM = r.ReadUInt32(), Stock = r.ReadUInt32() };
            await WaitForValidation(() => Resources.FindObjectsOfTypeAll<MainMenu>().Any(x => x.isActiveAndEnabled), 90f, "Main menu unavailable.");
            await UniTask.Delay(2000, ignoreTimeScale: true);
            await NetworkManagerNuclearOption.Preload(CancellationToken.None);
            var manager = NetworkManagerNuclearOption.i;
            Time.timeScale = 1f;
            if (role.StartsWith("host", StringComparison.Ordinal))
            {
                // Restrict test sockets to loopback before the native host starts.
                Set(Get<object>(manager, "udpTransport"), "Address", "::1");
                manager.Server.Started.AddListener(() =>
                    manager.Server.MessageHandler.RegisterHandler<ValidationMessage>((peer, message) =>
                    { validationPeer = peer; validationSnapshot = message; validationStage = message.Stage;
                      Debug.Log("A19J MP TEST RECEIVED stage=" + message.Stage + ", stock=" + message.Stock); }, allowUnauthenticated: false));
                var keys = MissionGroup.BuiltIn.GetMissions().Concat(MissionGroup.Default.GetMissions()).ToArray();
                var key = keys.OrderByDescending(x => x.Name.IndexOf("free", StringComparison.OrdinalIgnoreCase) >= 0).First();
                if (!key.TryLoad(out var mission, out string error)) throw new InvalidOperationException("Stock test mission failed to load: " + error);
                Debug.Log("A19J MP TEST MISSION: " + key.Name);
                MissionManager.SetMission(mission, false);
                var options = new HostOptions(SocketType.UDP, GameState.Multiplayer, mission.MapKey)
                { UdpPort = 28791, MaxConnections = 2 };
                await manager.StartHostAsync(options);
                await WaitForValidation(() => MissionManager.i != null && MissionManager.IsRunning, 120f, "Private native mission did not start.");
                if (role == "host-negative")
                {
                    Debug.Log("A19J MP HOST READY: loopback-only negative compatibility test.");
                    await WaitForValidation(() => compatibilityRejections > 0, 100f, "Missing/mismatched client was not rejected.");
                    if (manager.Server.AllPlayers.Any(x => !x.IsHost && x.IsAuthenticated))
                        throw new InvalidOperationException("Rejected peer reached authenticated mission state.");
                    Debug.Log("A19J MP REJECTION VALIDATION PASSED: rejected before remote mission/player spawn.");
                    return;
                }
                var hq = Resources.FindObjectsOfTypeAll<FactionHQ>().First(x => x.IsServer && x.faction != null && x.isActiveAndEnabled);
                hq.ModifyUnitSupply(Original, 3);
                if (hq.GetUnitSupply(Original) != hq.GetUnitSupply(Variant)) throw new InvalidOperationException("Mission inventory was not shared.");
                var aircraft = (Aircraft)AccessTools.Method(typeof(Spawner), "SpawnAircraft").Invoke(Spawner.i, new object[]
                { null, Variant.unitPrefab, null, 1f, default(LiveryKey), new GlobalPosition(0f, 2000f, 0f), Quaternion.identity, Vector3.zero, null, hq, "A19J localhost validation", 1f, 0.5f });
                foreach (var body in Get<List<UnitPart>>(aircraft, "partLookup").Where(x => x != null && x.rb != null).Select(x => x.rb).Distinct())
                    body.constraints = RigidbodyConstraints.FreezeAll;
                await UniTask.Delay(1500, ignoreTimeScale: true);
                var identity = aircraft.GetComponent<NetworkIdentity>();
                if (!aircraft.IsServer || identity.NetId == 0) throw new InvalidOperationException("Host aircraft was not network spawned.");
                Debug.Log("A19J MP HOST READY: loopback-only; A-19J already spawned for late-join test. NetId=" + identity.NetId);
                await WaitForValidation(() => validationStage >= 1 && validationPeer != null, 100f, "Remote client did not authenticate and prepare its scene.");
                var remotePlayer = Resources.FindObjectsOfTypeAll<Player>().First(x => x.IsServer && x.Owner == validationPeer);
                remotePlayer.SetFaction(hq, true);
                manager.ServerObjectManager.SpawnVisibleObjects(validationPeer, true);
                AccessTools.Method(typeof(ServerObjectManager), "SendSpawnMessage").Invoke(manager.ServerObjectManager,
                    new object[] { identity, validationPeer });
                await WaitForValidation(() => validationStage >= 2, 30f, "Remote client did not instantiate A-19J.");
                if (validationSnapshot.NetId != identity.NetId || validationSnapshot.Engines != 2 || validationSnapshot.Stock != (uint)hq.GetUnitSupply(Variant))
                    throw new InvalidOperationException("Remote prefab identity or engine layout differs.");
                await RunupValidationMode(aircraft).ToUniTask();
                if (spawnValidationFailed) throw new InvalidOperationException("Host engine run-up failed.");
                aircraft.NetworkIgnition = true;
                foreach (var jet in aircraft.engineStates.OfType<Turbojet>()) Get<ControlInputs>(jet, "controlInputs").throttle = 1f;
                uint hostRpm = (uint)(aircraft.engineStates.OfType<Turbojet>().Min(x => x.GetRPMRatio()) * 1000);
                validationPeer.Send(new ValidationMessage { Stage = 3, NetId = identity.NetId, RPM = hostRpm });
                await WaitForValidation(() => validationStage >= 4, 20f, "Remote engine state did not arrive.");
                Debug.Log("A19J MP REMOTE SNAPSHOT: NetId=" + validationSnapshot.NetId + ", engines=" + validationSnapshot.Engines + ", RPMx1000=" + validationSnapshot.RPM);
                if (validationSnapshot.Engines != 2 || validationSnapshot.RPM < 100 || Math.Abs((int)validationSnapshot.RPM - (int)hostRpm) > 200)
                    throw new InvalidOperationException("Remote jet RPM did not follow the host.");
                manager.ServerObjectManager.Destroy(identity);
                await WaitForValidation(() => validationStage >= 5, 20f, "Remote A-19J was not despawned.");
                Debug.Log("A19J MP HOST VALIDATION PASSED: native UDP authentication, version/catalog handshake, late-join prefab, two remote jets, engine state and despawn.");
                validationPeer.Send(new ValidationMessage { Stage = 6 });
                await UniTask.Delay(1000, ignoreTimeScale: true);
            }
            else
            {
                // Use the native join/map/mission path, including scene objects,
                // faction inventory, environment, fuel and input batching.
                manager.Client.Started.AddListener(() =>
                    manager.Client.MessageHandler.RegisterHandler<ValidationMessage>((peer, message) => validationStage = message.Stage, allowUnauthenticated: false));
                manager.StartClient(new ConnectOptions(SocketType.UDP, "::1", 28791));
                if (role == "mismatch" || role == "missing")
                {
                    await UniTask.Delay(15000, ignoreTimeScale: true);
                    if (manager.Client.Active && manager.Client.Player != null && manager.Client.Player.IsAuthenticated)
                        throw new InvalidOperationException("Incompatible test client authenticated.");
                    Debug.Log("A19J MP CLIENT REJECTION PASSED: " + role);
                    return;
                }
                await WaitForValidation(() => manager.Client.Player != null && manager.Client.Player.IsAuthenticated && serverCompatible, 30f, "Native client authentication/compatibility did not complete.");
                await WaitForValidation(() => !NetworkManagerNuclearOption.IsLoadingScene && MissionManager.i != null && MissionManager.IsRunning, 120f, "Client native map/mission did not load.");
                manager.Client.Player.Send(new ValidationMessage { Stage = 1 });
                Aircraft aircraft = null;
                await WaitForValidation(() =>
                {
                    aircraft = Resources.FindObjectsOfTypeAll<Aircraft>().FirstOrDefault(x => x.isActiveAndEnabled && x.IsClient && !x.IsServer && x.definition == Variant);
                    return aircraft != null && aircraft.engineStates.Count == 2;
                }, 30f, "Remote A-19J did not initialize.");
                var identity = aircraft.GetComponent<NetworkIdentity>();
                Time.timeScale = 1f;
                if (aircraft.GetComponentsInChildren<PropFan>(true).Length != 0) throw new InvalidOperationException("Client still has propellers.");
                manager.Client.Player.Send(new ValidationMessage { Stage = 2, NetId = identity.NetId, Engines = (uint)aircraft.engineStates.OfType<Turbojet>().Count(), Stock = (uint)aircraft.NetworkHQ.GetUnitSupply(Variant) });
                await WaitForValidation(() => validationStage == 3, 45f, "Host did not request engine snapshot.");
                uint rpm = (uint)(aircraft.engineStates.OfType<Turbojet>().Min(x => x.GetRPMRatio()) * 1000);
                Debug.Log("A19J MP CLIENT CONTEXT: timeScale=" + Time.timeScale + ", ignition=" + aircraft.Ignition
                    + ", density=" + aircraft.airDensity + ", fuel=" + aircraft.GetFuelQuantity() + ", throttle=" + aircraft.GetInputs().throttle);
                Debug.Log("A19J MP CLIENT SNAPSHOT: NetId=" + identity.NetId + ", RPMx1000=" + rpm);
                manager.Client.Player.Send(new ValidationMessage { Stage = 4, NetId = identity.NetId, Engines = 2, RPM = rpm });
                await WaitForValidation(() => aircraft == null, 25f, "Remote aircraft persisted after host despawn.");
                manager.Client.Player.Send(new ValidationMessage { Stage = 5 });
                await WaitForValidation(() => validationStage == 6, 10f, "Host did not confirm end of test.");
                Debug.Log("A19J MP CLIENT VALIDATION PASSED: real remote client with matching identity, two jets, native engine state and despawn.");
            }
        }
    }
}

