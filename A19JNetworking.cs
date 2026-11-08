using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Mirage;
using Mirage.Authentication;
using Mirage.Serialization;
using NuclearOption.Networking;
using UnityEngine;

namespace A19JConversion
{
    public sealed partial class A19JPlugin
    {
        // Fixed-size pre-auth message. No files, download requests, credentials or
        // player identifiers are included. The game still performs its normal auth.
        private struct CompatibilityMessage
        {
            public uint Kind;
            public uint Protocol;
            public uint DefinitionCount;
            public ulong Catalog;
        }

        private const uint NetworkProtocol = 0x00020000;
        private static bool serverCompatible;
        private static int compatibilityRejections;
        private static readonly Dictionary<INetworkPlayer, CompatibilityMessage> peerCompatibility =
            new Dictionary<INetworkPlayer, CompatibilityMessage>();

        private void InstallMultiplayer()
        {
            Writer<CompatibilityMessage>.Write = (writer, message) =>
            {
                writer.WriteUInt32(message.Kind);
                writer.WriteUInt32(message.Protocol);
                writer.WriteUInt32(message.DefinitionCount);
                writer.WriteUInt64(message.Catalog);
            };
            Reader<CompatibilityMessage>.Read = reader => new CompatibilityMessage
            {
                Kind = reader.ReadUInt32(), Protocol = reader.ReadUInt32(),
                DefinitionCount = reader.ReadUInt32(), Catalog = reader.ReadUInt64()
            };
            var auth = AccessTools.TypeByName("NuclearOption.Networking.Authentication.NetworkAuthenticatorNuclearOption");
            harmony.Patch(AccessTools.Method(auth, "OnStartServer"), postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(ServerAuthStarted)));
            harmony.Patch(AccessTools.Method(auth, "OnClientConnected"),
                prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(ClientAuthStarting)),
                postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(ClientAuthStarted)));
            harmony.Patch(AccessTools.Method(auth, "AuthenticateAsync"), postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(ExtendAuthentication)));
            harmony.Patch(AccessTools.Method(auth, "OnServerDisconnected"), postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(PeerDisconnected)));
            harmony.Patch(AccessTools.Method(typeof(NetworkManagerNuclearOption), "ClientStarted"), postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(RegisterNetworkPrefab)));
        }

        private static bool IsAuthoritySession()
        {
            return sessionConfigured && NetworkManagerNuclearOption.IsLoaded
                && NetworkManagerNuclearOption.i.Server != null && NetworkManagerNuclearOption.i.Server.Active;
        }

        private static bool IsSupplyAuthority(FactionHQ hq)
        {
            return hq != null && IsAuthoritySession() && hq.IsServer && !hq.AircraftSupply.IsReadOnly;
        }

        private static bool CanSelectVariant()
        {
            return sessionConfigured && (offlineConfigured || IsAuthoritySession() || serverCompatible);
        }

        private static void RegisterNetworkPrefab()
        {
            if (Variant == null || !NetworkManagerNuclearOption.IsLoaded) return;
            var manager = NetworkManagerNuclearOption.i;
            if (manager.ClientObjectManager != null)
                manager.ClientObjectManager.RegisterPrefab(Variant.unitPrefab.GetComponent<NetworkIdentity>());
        }

        private static void HashText(ref ulong hash, string text)
        {
            // FNV-1a is a deterministic compatibility checksum, not authentication.
            // Separate fields so concatenations cannot hide a different catalog.
            unchecked
            {
                foreach (char c in text ?? "") { hash ^= c; hash *= 1099511628211UL; }
                hash ^= 0xffff; hash *= 1099511628211UL;
            }
        }

        private static CompatibilityMessage LocalCompatibility(uint kind)
        {
            ulong hash = 14695981039346656037UL;
            HashText(ref hash, Version);
            foreach (var definition in Encyclopedia.i.IndexLookup)
            {
                HashText(ref hash, definition.GetType().FullName);
                HashText(ref hash, definition is UnitDefinition unit ? unit.jsonKey : (definition as UnityEngine.Object)?.name);
                HashText(ref hash, definition.LookupIndex.ToString());
                if (definition is UnitDefinition pref && pref.unitPrefab != null)
                {
                    var identity = pref.unitPrefab.GetComponent<NetworkIdentity>();
                    HashText(ref hash, identity != null ? identity.PrefabHash.ToString() : "none");
                }
            }
            HashText(ref hash, PrefabHash.ToString());
            if (Variant != null)
                foreach (var behaviour in Variant.unitPrefab.GetComponent<NetworkIdentity>().NetworkBehaviours)
                    HashText(ref hash, behaviour.GetType().FullName);
            return new CompatibilityMessage
            { Kind = kind, Protocol = NetworkProtocol, DefinitionCount = (uint)Encyclopedia.i.IndexLookup.Count, Catalog = hash };
        }

        private static bool Compatible(CompatibilityMessage remote)
        {
            if (Variant == null || remote.Protocol != NetworkProtocol) return false;
            var local = LocalCompatibility(0);
            return local.DefinitionCount == remote.DefinitionCount && local.Catalog == remote.Catalog;
        }

        private static void ServerAuthStarted()
        {
            peerCompatibility.Clear();
            NetworkManagerNuclearOption.i.Server.MessageHandler.RegisterHandler<CompatibilityMessage>(ReceiveCompatibility, allowUnauthenticated: true);
        }

        private static void ClientAuthStarting()
        {
            NetworkManagerNuclearOption.i.Client.MessageHandler.RegisterHandler<CompatibilityMessage>(ReceiveServerCompatibility, allowUnauthenticated: true);
        }

        private static void ClientAuthStarted(INetworkPlayer __0)
        {
            if (__0.IsHost) return;
            var offer = LocalCompatibility(0);
            // Explicit disposable test roles exercise real rejection messages.
            string test = Environment.GetEnvironmentVariable("A19J_VALIDATE") == "1"
                ? Environment.GetEnvironmentVariable("A19J_VALIDATE_MULTIPLAYER") : null;
            if (test == "missing") return;
            if (test == "mismatch") offer.Protocol++;
            __0.Send(offer);
        }

        private static void ReceiveCompatibility(INetworkPlayer player, CompatibilityMessage message)
        {
            if (message.Kind != 0 || player.IsHost || !player.IsConnected) return;
            // First offer wins for this connection; never allow later packets to
            // replace a successful compatibility decision while a mission runs.
            if (peerCompatibility.ContainsKey(player)) return;
            peerCompatibility.Add(player, message);
            player.Send(LocalCompatibility(1));
        }

        private static void ReceiveServerCompatibility(INetworkPlayer player, CompatibilityMessage message)
        {
            if (message.Kind != 1 || player.IsHost) return;
            serverCompatible = Compatible(message);
            AddToLoadedHangars();
            instance.Logger.LogInfo(serverCompatible
                ? "A19J multiplayer: host version and ordered definition catalog match."
                : "A19J multiplayer: host mismatch; install matching A-19J and aircraft mods on both PCs.");
        }

        private static void PeerDisconnected(INetworkPlayer __0) { peerCompatibility.Remove(__0); }

        private static void ExtendAuthentication(object __instance, INetworkPlayer __0,
            CancellationToken __2, ref UniTask<AuthenticationResult> __result)
        {
            // Preserve all stock Steam/build/password/ban checks and their result.
            // The additional check finishes before the native authenticated event
            // can send mission data, inventory keys or aircraft to the remote peer.
            __result = CheckCompatibilityAfterNative(__result, __0, (INetworkAuthenticator)__instance, __2);
        }

        private static async UniTask<AuthenticationResult> CheckCompatibilityAfterNative(
            UniTask<AuthenticationResult> native, INetworkPlayer player, INetworkAuthenticator authenticator, CancellationToken cancellation)
        {
            var result = await native;
            if (!result.Success || player.IsHost || !IsAuthoritySession()) return result;
            float deadline = Time.realtimeSinceStartup + 8f;
            while (!peerCompatibility.ContainsKey(player) && player.IsConnected && Time.realtimeSinceStartup < deadline)
                await UniTask.Delay(50, ignoreTimeScale: true, cancellationToken: cancellation);
            if (peerCompatibility.TryGetValue(player, out var remote) && Compatible(remote))
            {
                instance.Logger.LogInfo("A19J multiplayer: peer compatibility passed before mission transfer.");
                return result;
            }
            string reason = peerCompatibility.ContainsKey(player)
                ? "A-19J mismatch. Install A19J " + Version + " and the same aircraft mods on both PCs."
                : "This host requires A19J " + Version + ". Update A-19J and Blueprinter before joining.";
            instance.Logger.LogWarning("A19J multiplayer: " + reason);
            compatibilityRejections++;
            return AuthenticationResult.CreateFail(reason, authenticator);
        }

        private void ValidateNetworkRegistration()
        {
            var encyclopedia = Encyclopedia.i;
            var registered = encyclopedia.IndexLookup.ToArray();
            foreach (SocketType mode in Enum.GetValues(typeof(SocketType)))
            {
                NetworkConfigured(mode); NetworkConfigured(mode); SessionStopped();
                if (!encyclopedia.IndexLookup.SequenceEqual(registered)
                    || encyclopedia.IndexLookup.Count(x => ReferenceEquals(x, Variant)) != 1
                    || !encyclopedia.aircraft.Contains(Variant) || Encyclopedia.Lookup[AircraftKey] != Variant)
                    throw new InvalidOperationException("Session transition changed A-19J definition indices: " + mode);
            }
            NetworkConfigured((SocketType)999);
            Aircraft result = null;
            if (GuardAircraftSpawn(Variant.unitPrefab, ref result))
                throw new InvalidOperationException("Unknown inactive session allowed a server spawn.");
            if (!GuardAircraftSpawn(Original.unitPrefab, ref result))
                throw new InvalidOperationException("Stock A-19 was blocked.");
            var offer = LocalCompatibility(0);
            if (!Compatible(offer)) throw new InvalidOperationException("Matching compatibility offer was rejected.");
            offer.Protocol++;
            if (Compatible(offer)) throw new InvalidOperationException("Different version accepted.");
            offer = LocalCompatibility(0); offer.Catalog++;
            if (Compatible(offer)) throw new InvalidOperationException("Different catalog accepted.");
            offer = LocalCompatibility(0); offer.DefinitionCount++;
            if (Compatible(offer)) throw new InvalidOperationException("Different definition count accepted.");
            SessionStopped();
            Logger.LogInfo("A19J NETWORK REGISTRATION VALIDATION PASSED: stable definition across all six socket modes and disconnect; matching catalog accepted, version/catalog/count mismatches rejected, inactive spawn blocked and stock A-19 preserved.");
        }
    }
}
