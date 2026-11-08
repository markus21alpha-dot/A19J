using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace A19JConversion
{
    [BepInPlugin(Guid, "A-19J Jet Conversion", Version)]
    [BepInProcess("NuclearOption.exe")]
    [BepInDependency("com.nikkorap.blueprinter", "2.1.1")]
    public sealed class A19JPlugin : BaseUnityPlugin
    {
        public const string Guid = "local.a19j.jetconversion";
        public const string Version = "0.1.3";
        public const string AircraftKey = "A19J";
        // A deterministic identity distinct from the stock aircraft. Registration
        // refuses to proceed on a collision, instead of changing another prefab.
        private const int PrefabHash = 0x4131394A;
        private static A19JPlugin instance;
        internal static AircraftDefinition Variant;
        internal static AircraftDefinition Original;
        private GameObject storage;
        private Harmony harmony;
        private bool building;
        private bool spawnValidationFailed;
        private static bool offlineConfigured;
        private static readonly HashSet<FactionHQ> sharedStockFactions = new HashSet<FactionHQ>();
        private static readonly HashSet<FactionHQ> inheritedRestrictions = new HashSet<FactionHQ>();
        private static readonly HashSet<FactionHQ> servicingRequests = new HashSet<FactionHQ>();
        private static readonly Dictionary<FactionHQ, bool> authoredStock = new Dictionary<FactionHQ, bool>();
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private void Awake()
        {
            instance = this;
            harmony = new Harmony(Guid);
            harmony.Patch(AccessTools.Method(typeof(Hangar), "Awake"), postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(HangarAwake)));
            // Both StartHostAsync and StartClient pass through this shared boundary.
            // In particular, an online listen host is NOT an offline session.
            harmony.Patch(AccessTools.Method(typeof(NetworkManagerNuclearOption), "ConfigureNetwork"), prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(NetworkConfigured)));
            harmony.Patch(AccessTools.Method(typeof(NetworkManagerNuclearOption), "StopAsync"), prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(SessionStopped)));
            harmony.Patch(AccessTools.Method(typeof(NetworkManagerNuclearOption), "ServerStopped"), prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(SessionStopped)));
            harmony.Patch(AccessTools.Method(typeof(NetworkManagerNuclearOption), "ClientDisconnected"), prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(SessionStopped)));
            harmony.Patch(AccessTools.Method(typeof(Spawner), "SpawnAircraft"), prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(GuardAircraftSpawn)));
            harmony.Patch(AccessTools.Method(typeof(FactionHQ), "OnMissionLoad"), prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(BeginFactionLoad)), postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(FactionLoaded)));
            harmony.Patch(AccessTools.Method(typeof(FactionHQ), "OnDestroy"), prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(FactionLoading)));
            harmony.Patch(AccessTools.Method(typeof(FactionHQ), "ModifyUnitSupply"), prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(SupplyChanging)), postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(SupplyChanged)));
            harmony.Patch(AccessTools.Method(typeof(FactionHQ), "GetUnitSupply"), prefix: new HarmonyMethod(typeof(A19JPlugin), nameof(SupplyReading)));
            harmony.Patch(AccessTools.Method(typeof(FactionHQ), "AddSupplyUnit"), postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(SupplyDelivered)));
            Blueprinter.Api.StateChanged += OnBlueprinterState;
            SceneManager.sceneLoaded += OnSceneLoaded;
            if (Blueprinter.Api.CurrentState == Blueprinter.Api.State.Ready) Build();
            if (Environment.GetEnvironmentVariable("A19J_VALIDATE") == "1")
            {
                Application.runInBackground = true;
                Logger.LogInfo("A19J background validation mode enabled.");
                StartCoroutine(ValidationMode());
            }
        }

        private void OnDestroy()
        {
            Blueprinter.Api.StateChanged -= OnBlueprinterState;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            harmony?.UnpatchSelf();
        }

        private void OnBlueprinterState(Blueprinter.Api.State state)
        {
            if (state == Blueprinter.Api.State.Ready) Build();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Variant != null)
            {
                AddToLoadedHangars();
                if (IsOfflineSession())
                    foreach (var hq in Resources.FindObjectsOfTypeAll<FactionHQ>())
                        if (hq.gameObject.scene.isLoaded && hq.IsServer) FactionLoaded(hq);
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, Fields | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }

        private static T Get<T>(object target, string field)
        {
            var value = FindField(target.GetType(), field).GetValue(target);
            if (value == null) return default(T);
            if (value is T typed) return typed;
            throw new InvalidCastException(target.GetType().Name + "." + field + " is " + value.GetType().Name + ", expected " + typeof(T).Name);
        }
        private static void Set(object target, string field, object value) => FindField(target.GetType(), field).SetValue(target, value);

        private static void CopyEngineSettings(Turbojet source, Turbojet target)
        {
            // Copy only Unity-serialized Turbojet settings; delegates, live state,
            // controls and network objects are deliberately not copied.
            foreach (var field in typeof(Turbojet).GetFields(Fields | BindingFlags.DeclaredOnly))
            {
                if (field.IsStatic || field.IsInitOnly || field.IsNotSerialized) continue;
                if (!field.IsPublic && !field.IsDefined(typeof(SerializeField), true)) continue;
                object value = field.GetValue(source);
                if (value is AnimationCurve curve)
                    value = new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
                else if (value is Array array) value = array.Clone();
                field.SetValue(target, value);
            }
        }

        private static void CopyAudio(AudioSource source, AudioSource target)
        {
            target.clip = source.clip;
            target.outputAudioMixerGroup = source.outputAudioMixerGroup;
            target.volume = source.volume;
            target.pitch = source.pitch;
            target.loop = source.loop;
            target.playOnAwake = source.playOnAwake;
            target.spatialBlend = source.spatialBlend;
            target.dopplerLevel = source.dopplerLevel;
            target.rolloffMode = source.rolloffMode;
            target.minDistance = source.minDistance;
            target.maxDistance = source.maxDistance;
            target.priority = source.priority;
            foreach (AudioSourceCurveType type in Enum.GetValues(typeof(AudioSourceCurveType)))
            {
                var curve = source.GetCustomCurve(type);
                if (curve != null) target.SetCustomCurve(type, new AnimationCurve(curve.keys));
            }
        }

        private void Build()
        {
            if (Variant != null || building) return;
            building = true;
            AircraftDefinition definition = null;
            try
            {
                var encyclopedia = Encyclopedia.i;
                Original = encyclopedia.aircraft.Single(x => x.jsonKey == "CAS1");
                var compass = encyclopedia.aircraft.Single(x => x.jsonKey == "trainer");
                if (encyclopedia.aircraft.Any(x => x.jsonKey == AircraftKey))
                    throw new InvalidOperationException("An aircraft already uses key A19J.");
                if (Resources.FindObjectsOfTypeAll<Mirage.NetworkIdentity>().Any(x => x.PrefabHash == PrefabHash))
                    throw new InvalidOperationException("The A-19J prefab identity is already in use.");

                // Build under an inactive parent so no aircraft/engine lifecycle
                // methods run until a normal game spawn clones the finished prefab.
                storage = new GameObject("A19J_PrefabStorage");
                storage.SetActive(false);
                Object.DontDestroyOnLoad(storage);
                var prefab = Object.Instantiate(Original.unitPrefab, storage.transform, false);
                prefab.name = AircraftKey;
                prefab.SetActive(true);

                definition = Object.Instantiate(Original);
                definition.name = AircraftKey;
                definition.jsonKey = AircraftKey;
                definition.code = "A-19J";
                definition.unitName = "A-19J";
                definition.WorkshopTag = "A-19J";
                definition.description = "A jet-powered A-19 attack variant. Twin non-afterburning jets retain the Brawler's weapons, armour and redundant airframe systems, with subsonic strike performance.";
                definition.unitPrefab = prefab;
                definition.aircraftParameters = Object.Instantiate(Original.aircraftParameters);
                definition.aircraftParameters.name = "A19JParameters";
                definition.aircraftParameters.aircraftName = "A-19J";
                definition.aircraftParameters.maxSpeed = 255f;
                definition.aircraftParameters.cruiseThrottle = 0.78f;
                definition.aircraftInfo.maxSpeed = 920f;
                Set(prefab.GetComponent<Aircraft>(), "definition", definition);
                prefab.GetComponent<Mirage.NetworkIdentity>().PrefabHash = PrefabHash;

                var donors = compass.unitPrefab.GetComponentsInChildren<Turbojet>(true);
                var originalEngines = prefab.GetComponentsInChildren<TurbineEngine>(true);
                if (donors.Length != 2 || originalEngines.Length != 2)
                    throw new InvalidOperationException("Expected two A-19 propfan engines and two Compass jets.");

                foreach (var oldEngine in originalEngines)
                {
                    bool left = oldEngine.name.EndsWith("_L", StringComparison.Ordinal);
                    var donor = donors.Single(x => x.name.EndsWith(left ? "_L" : "_R", StringComparison.Ordinal));
                    ConvertEngine(prefab, oldEngine, donor, left);
                }
                ValidatePrefab(definition);

                Variant = definition;
                SetAvailability(offlineConfigured);
                Logger.LogInfo("A19J BUILD CHECK PASSED: separate A-19J built for offline registration; 2 jets, no propfans, original A-19 unchanged.");
                Logger.LogInfo("A19J EXHAUST VISUAL CHECK PASSED: complete housings, circular lips and recessed interiors registered at near and distant LODs.");
                Logger.LogInfo("A19J settings: 2 x 48 kN, no afterburners, stock A-19 weapons/armour; single-player flight validation required.");
            }
            catch (Exception ex)
            {
                Logger.LogError("A19J was not enabled: " + ex);
                if (storage != null) Object.Destroy(storage);
                if (definition != null) Object.Destroy(definition);
            }
            finally { building = false; }
        }

        private static void ConvertEngine(GameObject prefab, TurbineEngine oldEngine, Turbojet donor, bool left)
        {
            var engineObject = oldEngine.gameObject;
            var enginePart = engineObject.GetComponent<UnitPart>();
            var fan = engineObject.GetComponentInChildren<PropFan>(true);
            if (enginePart == null || fan == null) throw new InvalidOperationException("A-19 engine damage part or fan missing.");
            var hub = fan.transform;
            var hubPart = hub.GetComponent<UnitPart>();
            if (hubPart == null) throw new InvalidOperationException("A-19 hub damage part missing.");
            foreach (var system in Get<ParticleSystem[]>(oldEngine, "heatHazeParticles"))
                if (system != null) system.gameObject.SetActive(false);

            foreach (Transform child in hub)
                child.gameObject.SetActive(false);
            var hubFilter = hub.GetComponent<MeshFilter>();
            if (hubFilter != null) hubFilter.sharedMesh = null;
            var fanAudio = hub.GetComponent<AudioSource>();
            if (fanAudio != null) { fanAudio.enabled = false; fanAudio.playOnAwake = false; }
            foreach (var strike in hub.GetComponentsInChildren<PropStrikeDetector>(true)) Object.DestroyImmediate(strike);
            Object.DestroyImmediate(fan);
            foreach (var transmission in engineObject.GetComponents<Transmission>()) Object.DestroyImmediate(transmission);
            Object.DestroyImmediate(oldEngine);

            var jet = engineObject.AddComponent<Turbojet>();
            CopyEngineSettings(donor, jet);
            var audio = engineObject.GetComponent<AudioSource>();
            CopyAudio(Get<AudioSource>(donor, "turbineAudio"), audio);
            Set(jet, "turbineAudio", audio);

            var donorNozzle = Get<JetNozzle[]>(donor, "nozzles").Single();
            var nozzleObject = Object.Instantiate(donorNozzle.gameObject, hub, false);
            nozzleObject.name = left ? "A19J_nozzle_L" : "A19J_nozzle_R";
            // Match the existing nacelle opening, keeping its original intake,
            // texture and damage part. The donor mesh extends forward internally.
            var desiredPosition = new Vector3(0f, 0f, -1.90f);
            nozzleObject.transform.localPosition = hub.InverseTransformPoint(engineObject.transform.TransformPoint(desiredPosition));
            nozzleObject.transform.localRotation = Quaternion.identity;
            nozzleObject.transform.localScale = new Vector3(0.75f, 0.75f, 0.40f);
            // The Compass attaches its tail surfaces under the nozzle part.
            // Keep only the exhaust/heat-haze children, never those surfaces.
            foreach (Transform child in nozzleObject.transform.Cast<Transform>().ToArray())
                if (!child.name.StartsWith("nozzletransform_", StringComparison.Ordinal) && !child.name.Contains("lod1"))
                    Object.DestroyImmediate(child.gameObject);
            // The donor exhaust is shaped to join a different fuselage. Replace
            // its exterior mesh with a complete round housing fitted to this nacelle.
            // Keep its original thrust transform, sound and particle systems.
            foreach (var renderer in nozzleObject.GetComponentsInChildren<MeshRenderer>(true))
                Object.DestroyImmediate(renderer);
            AddExhaustHousing(engineObject.transform, nozzleObject.transform, left);
            foreach (var part in nozzleObject.GetComponentsInChildren<UnitPart>(true)) Object.DestroyImmediate(part);
            var nozzle = nozzleObject.GetComponent<JetNozzle>();
            Set(nozzle, "part", hubPart);
            Set(nozzle, "turbojet", jet);
            Set(nozzle, "engine", null);
            Set(nozzle, "vectorTransforms", Array.Empty<Transform>());
            Set(nozzle, "afterburners", Array.CreateInstance(FindField(typeof(JetNozzle), "afterburners").FieldType.GetElementType(), 0));
            Set(jet, "nozzles", new[] { nozzle });
            Set(jet, "criticalParts", new[] { enginePart, hubPart });
            Set(jet, "vectoringTransforms", Array.Empty<Transform>());
            Set(jet, "thrustVectoring", Vector3.zero);
            Set(jet, "splitThrustFactor", 0f);
            Set(jet, "maxSpeed", 235f);
            Set(jet, "fuelConsumptionMin", Get<float>(donor, "fuelConsumptionMin") * (48000f / donor.maxThrust));
            Set(jet, "fuelConsumptionMax", Get<float>(donor, "fuelConsumptionMax") * (48000f / donor.maxThrust));
            Set(jet, "failureMessage", left ? "LEFT ENGINE FIRE" : "RIGHT ENGINE FIRE");
            jet.maxThrust = 48000f;
            AddNozzleToLods(prefab, nozzleObject);
            var aircraft = prefab.GetComponent<Aircraft>();
            var exterior = Get<Renderer[]>(aircraft, "exteriorRenderers") ?? Array.Empty<Renderer>();
            Set(aircraft, "exteriorRenderers", exterior.Concat(nozzleObject.GetComponentsInChildren<MeshRenderer>(true)).Distinct().ToArray());
            var globalParticles = prefab.GetComponent<SetGlobalParticles>();
            if (globalParticles != null)
            {
                var systems = Get<List<ParticleSystem>>(globalParticles, "systems") ?? new List<ParticleSystem>();
                Set(globalParticles, "systems", systems.Concat(nozzleObject.GetComponentsInChildren<ParticleSystem>(true)).Distinct().ToList());
            }
        }

        private static readonly Vector2[] FairingProfile =
        {
            // Engine-local axial position, radius. The closed profile gives the
            // housing both an outside and an inside, including the exhaust lip.
            new Vector2(-1.28f, 0.525f), new Vector2(-1.32f, 0.550f),
            new Vector2(-1.46f, 0.550f), new Vector2(-1.51f, 0.540f),
            new Vector2(-1.73f, 0.500f), new Vector2(-1.98f, 0.450f),
            new Vector2(-2.24f, 0.407f), new Vector2(-2.36f, 0.400f),
            new Vector2(-2.39f, 0.386f), new Vector2(-2.39f, 0.345f),
            new Vector2(-2.35f, 0.335f), new Vector2(-1.55f, 0.360f),
            new Vector2(-1.28f, 0.495f)
        };
        private static readonly Vector2[] LipProfile =
        {
            new Vector2(-2.32f, 0.404f), new Vector2(-2.37f, 0.402f),
            new Vector2(-2.405f, 0.386f), new Vector2(-2.405f, 0.345f),
            new Vector2(-2.37f, 0.332f), new Vector2(-2.32f, 0.336f)
        };
        private static readonly Vector2[] LinerProfile =
        {
            new Vector2(-2.34f, 0.333f), new Vector2(-1.70f, 0.352f),
            new Vector2(-1.70f, 0.348f), new Vector2(-2.34f, 0.329f)
        };
        private static readonly Vector2[] CoreProfile =
        {
            new Vector2(-1.69f, 0.350f), new Vector2(-1.72f, 0.350f),
            new Vector2(-1.72f, 0.125f), new Vector2(-1.93f, 0.072f),
            new Vector2(-2.05f, 0.022f), new Vector2(-2.06f, 0.001f),
            new Vector2(-1.69f, 0.001f)
        };

        private static Material ExhaustMaterial(string name, Color color, float metallic, float smoothness)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("The game's standard lit shader was not found.");
            var material = new Material(shader) { name = "A19J_" + name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.enableInstancing = true;
            return material;
        }

        private static void AddExhaustHousing(Transform engine, Transform nozzle, bool left)
        {
            var fairing = ExhaustMaterial("ExhaustFairing", new Color(0.18f, 0.19f, 0.20f, 1f), 0.72f, 0.38f);
            var lip = ExhaustMaterial("ExhaustLip", new Color(0.30f, 0.29f, 0.27f, 1f), 0.88f, 0.47f);
            var liner = ExhaustMaterial("ExhaustLiner", new Color(0.052f, 0.056f, 0.060f, 1f), 0.55f, 0.24f);
            var core = ExhaustMaterial("ExhaustCore", new Color(0.075f, 0.072f, 0.068f, 1f), 0.65f, 0.30f);
            for (int lod = 0; lod < 2; ++lod)
            {
                int segments = lod == 0 ? 96 : 32;
                string suffix = (left ? "_L" : "_R") + (lod == 0 ? "" : "_lod1");
                AddLathedSurface(engine, nozzle, "A19J_fairing" + suffix, FairingProfile, segments, fairing);
                AddLathedSurface(engine, nozzle, "A19J_lip" + suffix, LipProfile, segments, lip);
                AddLathedSurface(engine, nozzle, "A19J_liner" + suffix, LinerProfile, segments, liner);
                AddLathedSurface(engine, nozzle, "A19J_core" + suffix, CoreProfile, segments, core);
            }
        }

        private static void AddLathedSurface(Transform engine, Transform parent, string name, Vector2[] profile, int segments, Material material)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uv = new List<Vector2>();
            // Each profile span has its own vertices, preserving deliberate bevel
            // edges; circumference normals are continuous across the closing seam.
            for (int span = 0; span < profile.Length; ++span)
            {
                var a = profile[span];
                var b = profile[(span + 1) % profile.Length];
                int start = vertices.Count;
                for (int i = 0; i <= segments; ++i)
                {
                    float angle = 2f * Mathf.PI * i / segments;
                    foreach (var p in new[] { a, b })
                    {
                        var point = new Vector3(Mathf.Cos(angle) * p.y, Mathf.Sin(angle) * p.y, p.x);
                        vertices.Add(parent.InverseTransformPoint(engine.TransformPoint(point)));
                        uv.Add(new Vector2((float)i / segments, p.x));
                    }
                }
                for (int i = 0; i < segments; ++i)
                {
                    int n = start + i * 2;
                    triangles.AddRange(new[] { n, n + 1, n + 2, n + 2, n + 1, n + 3 });
                }
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            // The duplicated UV seam must not create a visible lighting split.
            var normals = mesh.normals;
            int stride = (segments + 1) * 2;
            for (int span = 0; span < profile.Length; ++span)
                for (int edge = 0; edge < 2; ++edge)
                {
                    int first = span * stride + edge, last = first + segments * 2;
                    normals[first] = normals[last] = (normals[first] + normals[last]).normalized;
                }
            mesh.normals = normals;
            mesh.RecalculateBounds();
            var part = new GameObject(name);
            part.layer = parent.gameObject.layer;
            part.transform.SetParent(parent, false);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            part.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void AddNozzleToLods(GameObject prefab, GameObject nozzle)
        {
            var group = prefab.GetComponent<LODGroup>();
            if (group == null) throw new InvalidOperationException("A-19 LOD group missing.");
            var levels = group.GetLODs();
            var meshes = nozzle.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < levels.Length; ++i)
            {
                var additional = meshes.Where(r => i == 0
                    ? !r.name.Contains("lod1")
                    : r.name.Contains("lod1")).Cast<Renderer>();
                levels[i].renderers = levels[i].renderers.Concat(additional).ToArray();
            }
            group.SetLODs(levels);
        }

        private static void ValidatePrefab(AircraftDefinition definition)
        {
            var prefab = definition.unitPrefab;
            if (definition == Original || prefab == Original.unitPrefab || definition.aircraftParameters == Original.aircraftParameters)
                throw new InvalidOperationException("Variant must own its definition, parameters and prefab.");
            if (prefab.GetComponentsInChildren<PropFan>(true).Length != 0 || prefab.GetComponentsInChildren<TurbineEngine>(true).Length != 0)
                throw new InvalidOperationException("A propeller engine remained in the jet variant.");
            var jets = prefab.GetComponentsInChildren<Turbojet>(true);
            if (jets.Length != 2) throw new InvalidOperationException("A-19J requires two jets.");
            foreach (var jet in jets)
            {
                var parts = Get<UnitPart[]>(jet, "criticalParts");
                if (parts.Length == 0 || parts.Any(x => x == null || !x.transform.IsChildOf(prefab.transform)))
                    throw new InvalidOperationException("Engine has a missing/foreign damage part.");
                foreach (var nozzle in Get<JetNozzle[]>(jet, "nozzles"))
                {
                    if (nozzle == null || Get<Turbojet>(nozzle, "turbojet") != jet || !nozzle.transform.IsChildOf(prefab.transform))
                        throw new InvalidOperationException("Engine nozzle wiring is invalid.");
                    if (((Array)FindField(typeof(JetNozzle), "afterburners").GetValue(nozzle)).Length != 0)
                        throw new InvalidOperationException("Afterburners are not allowed on A-19J.");
                    var renderers = nozzle.GetComponentsInChildren<MeshRenderer>(true);
                    var lods = prefab.GetComponent<LODGroup>().GetLODs();
                    if (renderers.Length != 8 || renderers.Any(x => x.sharedMaterial == null || x.GetComponent<MeshFilter>().sharedMesh == null))
                        throw new InvalidOperationException("An exhaust housing surface or material is missing.");
                    for (int level = 0; level < lods.Length; ++level)
                    {
                        var expected = renderers.Where(x => x.name.Contains("lod1") == (level > 0));
                        if (expected.Any(x => !lods[level].renderers.Contains(x)))
                            throw new InvalidOperationException("Exhaust housing disappears at a rendering distance.");
                    }
                }
            }
            if (Original.unitPrefab.GetComponentsInChildren<PropFan>(true).Length != 2)
                throw new InvalidOperationException("Original A-19 propfans were changed.");
        }

        private static void HangarAwake(Hangar __instance) { AddToHangar(__instance); }
        private static bool IsOfflineSession()
        {
            if (!offlineConfigured || !NetworkManagerNuclearOption.IsLoaded) return false;
            var manager = NetworkManagerNuclearOption.i;
            return manager.Server != null && manager.Server.Active && !manager.Server.Listening
                && manager.Client != null && manager.Client.IsHost;
        }

        private static void NetworkConfigured(SocketType __0)
        {
            offlineConfigured = __0 == SocketType.Offline;
            SetAvailability(offlineConfigured);
        }

        private static void SessionStopped()
        {
            offlineConfigured = false;
            SetAvailability(false);
        }

        private static void SetAvailability(bool available)
        {
            if (Variant == null) return;
            if (!available)
            {
                foreach (var hq in sharedStockFactions.ToArray()) FactionLoading(hq);
                authoredStock.Clear();
            }
            var encyclopedia = Encyclopedia.i;
            if (available)
            {
                if (!encyclopedia.IndexLookup.Contains(Variant))
                {
                    ((INetworkDefinition)Variant).LookupIndex = encyclopedia.IndexLookup.Count;
                    encyclopedia.IndexLookup.Add(Variant);
                }
                if (!encyclopedia.aircraft.Contains(Variant)) encyclopedia.aircraft.Add(Variant);
                Encyclopedia.Lookup[AircraftKey] = Variant;
            }
            else
            {
                encyclopedia.aircraft.Remove(Variant);
                if (Encyclopedia.Lookup.TryGetValue(AircraftKey, out var entry) && entry == Variant)
                    Encyclopedia.Lookup.Remove(AircraftKey);
                int removed = encyclopedia.IndexLookup.IndexOf(Variant);
                if (removed >= 0)
                {
                    encyclopedia.IndexLookup.RemoveAt(removed);
                    // Restore indices after removing our entry, including definitions
                    // another mod may have appended during the offline session.
                    for (int i = removed; i < encyclopedia.IndexLookup.Count; i++)
                        encyclopedia.IndexLookup[i].LookupIndex = i;
                }
                ((INetworkDefinition)Variant).LookupIndex = -1;
                if (NetworkManagerNuclearOption.IsLoaded)
                    NetworkManagerNuclearOption.i.ClientObjectManager.UnregisterPrefab(Variant.unitPrefab.GetComponent<Mirage.NetworkIdentity>());
            }
            Set(encyclopedia, "aircraftAndVehiclesCache", null);
            AddToLoadedHangars();
        }

        private static bool GuardAircraftSpawn(GameObject __1, ref Aircraft __result)
        {
            if (Variant == null || __1 != Variant.unitPrefab || IsOfflineSession()) return true;
            __result = null;
            instance.Logger.LogWarning("A-19J spawn blocked: this build requires a confirmed offline local-host session.");
            return false;
        }

        private static void AddToHangar(Hangar hangar)
        {
            if (Variant == null || Original == null) return;
            var list = Get<AircraftDefinition[]>(hangar, "availableAircraft");
            if (list == null) return;
            if (!offlineConfigured)
            {
                if (list.Contains(Variant)) Set(hangar, "availableAircraft", list.Where(x => x != Variant).ToArray());
                return;
            }
            if (!list.Contains(Original) || list.Contains(Variant)) return;
            Set(hangar, "availableAircraft", list.Concat(new[] { Variant }).ToArray());
        }
        private static void AddToLoadedHangars()
        {
            foreach (var hangar in Resources.FindObjectsOfTypeAll<Hangar>()) AddToHangar(hangar);
        }

        private static void FactionLoading(FactionHQ __instance)
        {
            if (sharedStockFactions.Remove(__instance) && __instance != null && Variant != null && !__instance.AircraftSupply.IsReadOnly)
                __instance.AircraftSupply.Remove(Variant);
            if (inheritedRestrictions.Remove(__instance) && __instance != null)
                __instance.restrictedAircraft.Remove(AircraftKey);
            servicingRequests.Remove(__instance);
            authoredStock.Remove(__instance);
        }

        private static void BeginFactionLoad(FactionHQ __instance, NuclearOption.SavedMission.Mission __0)
        {
            FactionLoading(__instance);
            if (!offlineConfigured) return;
            __0.GetFactionFromHq(__instance, out var settings);
            authoredStock[__instance] = settings.supplies.Any(x => x.UnitType == AircraftKey);
        }

        private static void FactionLoaded(FactionHQ __instance)
        {
            if (!IsOfflineSession() || Variant == null || Original == null || __instance == null) return;
            if (sharedStockFactions.Contains(__instance)) { SyncSharedStock(__instance); return; }
            // An authored A19J entry, including an explicit zero, remains independent.
            // Legacy missions instead offer A-19J as a conversion of an A-19 airframe.
            if (authoredStock.TryGetValue(__instance, out bool independent))
            {
                if (independent || (__instance.AircraftSupply.TryGetValue(Variant, out var supplied) && supplied.Count > 0)) return;
            }
            else if (__instance.AircraftSupply.ContainsKey(Variant)) return;
            sharedStockFactions.Add(__instance);
            if (__instance.restrictedAircraft.Contains(Original.jsonKey) && !__instance.restrictedAircraft.Contains(AircraftKey))
            {
                __instance.restrictedAircraft.Add(AircraftKey);
                inheritedRestrictions.Add(__instance);
            }
            SyncSharedStock(__instance);
        }

        private static void SyncSharedStock(FactionHQ hq)
        {
            if (!IsOfflineSession() || !sharedStockFactions.Contains(hq) || hq == null) return;
            int count = hq.AircraftSupply.TryGetValue(Original, out var supply) ? supply.Count : 0;
            if (!hq.AircraftSupply.TryGetValue(Variant, out var current) || current.Count != count)
                hq.AircraftSupply[Variant] = new FactionHQ.RuntimeSupply(count);
        }

        private static void SupplyReading(FactionHQ __instance, UnitDefinition __0)
        {
            if (__0 != Variant) return;
            // Also cover loading orders where the local host was not fully active
            // when the mission-load postfix ran, or the definition was built late.
            if (!sharedStockFactions.Contains(__instance) && authoredStock.ContainsKey(__instance)) FactionLoaded(__instance);
            SyncSharedStock(__instance);
        }

        private static void SupplyChanging(FactionHQ __instance, ref UnitDefinition __0)
        {
            if (IsOfflineSession() && __0 == Variant && sharedStockFactions.Contains(__instance))
                __0 = Original;
        }

        private static void SupplyChanged(FactionHQ __instance) { SyncSharedStock(__instance); }

        private static void SupplyDelivered(FactionHQ __instance, UnitDefinition __0, int __1)
        {
            SyncSharedStock(__instance);
            if (__1 <= 0 || (__0 != Original && __0 != Variant) || !IsOfflineSession()
                || !sharedStockFactions.Contains(__instance) || !servicingRequests.Add(__instance)) return;
            try
            {
                // A stock A-19 factory normally checks only A-19 reservation requests.
                // Retry queued conversions through the native request path once shared
                // stock exists, preserving the requested aircraft and native rank checks.
                var requests = Get<IDictionary>(__instance, "reserveRequests");
                var players = new List<Player>();
                foreach (DictionaryEntry request in requests)
                    if (request.Key is Player player && player != null && Get<AircraftDefinition>(request.Value, "aircraftDefinition") == Variant)
                        players.Add(player);
                foreach (var player in players.OrderByDescending(x => x.PlayerScore))
                {
                    if (__instance.GetUnitSupply(Variant) <= 0) break;
                    // Match the native AddSupplyUnit queue filter in this game version.
                    if (player.CanAffordAirframe(Variant)) continue;
                    requests.Remove(player);
                    __instance.RequestReservedAirframe(player, Variant);
                }
            }
            finally { servicingRequests.Remove(__instance); }
        }

        private void ValidateOfflineGuard()
        {
            SessionStopped();
            var encyclopedia = Encyclopedia.i;
            var baseline = encyclopedia.IndexLookup.ToArray();
            foreach (var mode in Enum.GetValues(typeof(SocketType)).Cast<SocketType>().Concat(new[] { (SocketType)999 }))
            {
                NetworkConfigured(SocketType.Offline);
                NetworkConfigured(SocketType.Offline);
                if (encyclopedia.IndexLookup.Count(x => ReferenceEquals(x, Variant)) != 1)
                    throw new InvalidOperationException("Offline registration was duplicated.");
                NetworkConfigured(mode);
                if (mode == SocketType.Offline) SessionStopped();
                Aircraft result = null;
                if (IsOfflineSession() || GuardAircraftSpawn(Variant.unitPrefab, ref result))
                    throw new InvalidOperationException("Non-offline/unknown session allowed A-19J: " + mode);
                if (!GuardAircraftSpawn(Original.unitPrefab, ref result))
                    throw new InvalidOperationException("The guard blocked the stock A-19.");
                if (!encyclopedia.IndexLookup.SequenceEqual(baseline) || encyclopedia.aircraft.Contains(Variant)
                    || Encyclopedia.Lookup.ContainsKey(AircraftKey))
                    throw new InvalidOperationException("Disabled A-19J left a registration behind.");
                foreach (var hangar in Resources.FindObjectsOfTypeAll<Hangar>())
                    if (Get<AircraftDefinition[]>(hangar, "availableAircraft")?.Contains(Variant) == true)
                        throw new InvalidOperationException("Disabled A-19J remained in a hangar.");
            }
            // Exercise a later definition without discarding or reordering it.
            var later = ScriptableObject.CreateInstance<AircraftDefinition>();
            try
            {
                NetworkConfigured(SocketType.Offline);
                ((INetworkDefinition)later).LookupIndex = encyclopedia.IndexLookup.Count;
                encyclopedia.IndexLookup.Add(later);
                SessionStopped();
                if (!ReferenceEquals(encyclopedia.IndexLookup.Last(), later) || ((INetworkDefinition)later).LookupIndex != baseline.Length)
                    throw new InvalidOperationException("Removing A-19J did not preserve a later definition.");
            }
            finally { encyclopedia.IndexLookup.Remove(later); Object.DestroyImmediate(later); }
            foreach (var name in new[] { "ConfigureNetwork", "StopAsync", "ServerStopped", "ClientDisconnected" })
                if (!Harmony.GetPatchInfo(AccessTools.Method(typeof(NetworkManagerNuclearOption), name)).Prefixes.Any(x => x.owner == Guid))
                    throw new InvalidOperationException("Missing session guard patch: " + name);
            Logger.LogInfo("A19J OFFLINE GUARD VALIDATION PASSED: all online socket modes and unknown mode denied, stock A-19 allowed, repeated registration/removal and later definition preserved.");
        }

        private void ValidateFactionInventory()
        {
            // Isolated native components in the explicit headless test process.
            // These inactive fixtures never open a socket or enter a live mission.
            var manager = NetworkManagerNuclearOption.i;
            var hqObject = new GameObject("A19J_Inventory_Test_HQ");
            hqObject.SetActive(false);
            var hqIdentity = hqObject.AddComponent<Mirage.NetworkIdentity>();
            var hq = hqObject.AddComponent<FactionHQ>();
            var factoryObject = new GameObject("A19J_Inventory_Test_Factory");
            factoryObject.SetActive(false);
            var factoryIdentity = factoryObject.AddComponent<Mirage.NetworkIdentity>();
            var building = factoryObject.AddComponent<Building>();
            var factory = factoryObject.AddComponent<Factory>();
            var faction = ScriptableObject.CreateInstance<Faction>();
            faction.factionName = "A19J isolated inventory test";
            hq.faction = faction;
            var player = Resources.FindObjectsOfTypeAll<Player>().First(x => x.isActiveAndEnabled && x.IsServer && x.Owner != null);
            var owned = player.OwnedAirframes.ToArray();
            float value = Variant.value;
            int rank = Variant.aircraftParameters.rankRequired;
            var previousMissionManager = MissionManager.i;
            GameObject missionObject = null;
            try
            {
                if (previousMissionManager == null)
                {
                    // The encyclopedia has no mission clock component, but the native
                    // Factory.ProduceUnit method records a production timestamp.
                    missionObject = new GameObject("A19J_Inventory_Test_Clock");
                    missionObject.SetActive(false);
                    missionObject.AddComponent<Mirage.NetworkIdentity>();
                    MissionManager.i = missionObject.AddComponent<MissionManager>();
                }
                AccessTools.Method(typeof(Mirage.NetworkIdentity), "SetServerValues").Invoke(hqIdentity, new object[] { manager.Server, manager.ServerObjectManager });
                AccessTools.Method(typeof(Mirage.NetworkIdentity), "SetServerValues").Invoke(factoryIdentity, new object[] { manager.Server, manager.ServerObjectManager });
                Set(hqIdentity, "<NetId>k__BackingField", 0x41313901u);
                Set(factoryIdentity, "<NetId>k__BackingField", 0x41313902u);
                building.NetworkHQ = hq;
                Set(factory, "attachedUnit", building);
                var settings = new NuclearOption.SavedMission.MissionFaction(faction.factionName);
                settings.supplies.Add(new NuclearOption.SavedMission.UnitCount(Original.jsonKey, 99));
                var mission = new NuclearOption.SavedMission.Mission();
                mission.factions.Add(settings);
                // Other installed mods assume a complete playable campaign in their
                // OnMissionLoad postfixes. Use the original native loader only for
                // this inactive fixture and apply our two hooks around it explicitly.
                // No third-party patches are removed or changed.
                harmony.CreateReversePatcher(AccessTools.Method(typeof(FactionHQ), "OnMissionLoad"),
                    new HarmonyMethod(typeof(A19JPlugin), nameof(LoadNativeFactionFixture))).Patch(HarmonyReversePatchType.Original);
                var loadPatches = Harmony.GetPatchInfo(AccessTools.Method(typeof(FactionHQ), "OnMissionLoad"));
                if (!loadPatches.Prefixes.Any(x => x.owner == Guid) || !loadPatches.Postfixes.Any(x => x.owner == Guid))
                    throw new InvalidOperationException("Faction inventory load hooks were not installed.");
                Action load = () => { BeginFactionLoad(hq, mission); LoadNativeFactionFixture(hq, mission); FactionLoaded(hq); };
                Action<UnitDefinition> produce = definition =>
                {
                    Set(factory, "productionUnit", definition);
                    AccessTools.Method(typeof(Factory), "ProduceUnit").Invoke(factory, null);
                };
                Action<int, int> expect = (original, variant) =>
                {
                    if (hq.GetUnitSupply(Original) != original || hq.GetUnitSupply(Variant) != variant)
                        throw new InvalidOperationException("Inventory mismatch; expected A-19/A-19J " + original + "/" + variant
                            + ", got " + hq.GetUnitSupply(Original) + "/" + hq.GetUnitSupply(Variant));
                };
                load(); expect(99, 99);
                Logger.LogInfo("A19J inventory check: native faction supply reports 99 A-19J airframes.");
                if (!hq.AircraftSupply.ContainsKey(Variant)) throw new InvalidOperationException("A-19J has no real faction inventory entry.");
                // Some loaders create zero placeholders for every registered aircraft.
                // They must not be mistaken for an authored zero in the mission file.
                BeginFactionLoad(hq, mission); LoadNativeFactionFixture(hq, mission);
                hq.AircraftSupply[Variant] = new FactionHQ.RuntimeSupply(0);
                FactionLoaded(hq); expect(99, 99);
                BeginFactionLoad(hq, mission); LoadNativeFactionFixture(hq, mission);
                expect(99, 99); // A first UI read must repair a deferred host setup.
                hq.ModifyUnitSupply(Variant, -1); expect(98, 98);
                hq.ModifyUnitSupply(Original, -1); expect(97, 97);
                produce(Original); expect(98, 98);
                produce(Variant); expect(99, 99);
                hq.ModifyUnitSupply(Original, -99); expect(0, 0);
                // Exercise the stock game's queue filter and rank path with explicit
                // temporary test values, then restore the released aircraft settings.
                Variant.value = float.MaxValue / 4f;
                Variant.aircraftParameters.rankRequired = 0;
                if (player.OwnsAirframe(Variant, true)) throw new InvalidOperationException("Test player already owns A-19J.");
                hq.RequestReservedAirframe(player, Variant);
                var requests = Get<IDictionary>(hq, "reserveRequests");
                if (!requests.Contains(player)) throw new InvalidOperationException("Native reservation did not queue.");
                produce(Original); expect(0, 0);
                if (player.OwnedAirframeTypeCount(Variant, true) != 1 || requests.Contains(player))
                    throw new InvalidOperationException("Factory refill did not grant exactly one queued A-19J and clear its request.");
                Variant.value = value;
                Variant.aircraftParameters.rankRequired = rank;
                settings.supplies.Add(new NuclearOption.SavedMission.UnitCount(AircraftKey, 7));
                load(); expect(99, 7);
                hq.ModifyUnitSupply(Variant, -1); expect(99, 6);
                produce(Original); expect(100, 6);
                produce(Variant); expect(100, 7);
                settings.supplies.Last().Count = 0;
                load(); expect(99, 0);
                produce(Original); expect(100, 0);
                settings.supplies.Clear();
                load(); expect(0, 0);
                produce(Original); expect(1, 1);
                settings.supplies.Add(new NuclearOption.SavedMission.UnitCount(Original.jsonKey, 99));
                settings.restrictions = new NuclearOption.SavedMission.Restrictions
                { aircraft = new List<string> { Original.jsonKey } };
                load(); expect(99, 99);
                if (!hq.restrictedAircraft.Contains(AircraftKey)) throw new InvalidOperationException("A-19 faction restriction was not inherited.");
                NetworkConfigured(SocketType.Steam);
                if (hq.AircraftSupply.ContainsKey(Variant) || hq.GetUnitSupply(Original) != 99)
                    throw new InvalidOperationException("Online transition did not remove only the shared inventory entry.");
                NetworkConfigured(SocketType.Offline);
                Logger.LogInfo("A19J INVENTORY VALIDATION PASSED: real 99 stock, shared consumption, native A-19/A-19J factory production, depleted supply, queued A-19J refill, explicit stock and zero preserved, faction restrictions inherited, online cleanup.");
            }
            finally
            {
                Variant.value = value;
                Variant.aircraftParameters.rankRequired = rank;
                player.OwnedAirframes.Clear();
                foreach (var airframe in owned) player.OwnedAirframes.Add(airframe);
                FactionLoading(hq);
                Set(hqIdentity, "<NetId>k__BackingField", 0u);
                Set(factoryIdentity, "<NetId>k__BackingField", 0u);
                Object.DestroyImmediate(factoryObject);
                Object.DestroyImmediate(hqObject);
                Object.DestroyImmediate(faction);
                MissionManager.i = previousMissionManager;
                if (missionObject != null) Object.DestroyImmediate(missionObject);
                NetworkConfigured(SocketType.Offline);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LoadNativeFactionFixture(FactionHQ hq, NuclearOption.SavedMission.Mission mission)
        {
            throw new InvalidOperationException("The explicit validation fixture has not installed its reverse patch.");
        }

        private IEnumerator ValidationMode()
        {
            float deadline = Time.realtimeSinceStartup + 180f;
            while (Variant == null && Time.realtimeSinceStartup < deadline) yield return null;
            int exitCode = 1;
            try
            {
                if (Variant == null) throw new InvalidOperationException("A-19J did not finish registration.");
                ValidateOfflineGuard();
                NetworkConfigured(SocketType.Offline);
                ValidatePrefab(Variant);
                var prefab = Variant.unitPrefab;
                if (prefab.activeInHierarchy) throw new InvalidOperationException("Stored aircraft prefab unexpectedly active.");
                if (prefab.GetComponentsInChildren<UnitPart>(true).Length != Original.unitPrefab.GetComponentsInChildren<UnitPart>(true).Length)
                    throw new InvalidOperationException("The conversion changed the airframe damage-part count.");
                var compass = Encyclopedia.i.aircraft.Single(x => x.jsonKey == "trainer");
                var seen = new HashSet<object>();
                foreach (var component in prefab.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) throw new InvalidOperationException("Missing script on A-19J.");
                    if (component.GetType().Assembly != typeof(Aircraft).Assembly) continue;
                    foreach (var field in component.GetType().GetFields(Fields))
                        if (!field.IsStatic && !field.IsNotSerialized && (field.IsPublic || field.IsDefined(typeof(SerializeField), true)))
                            CheckForeignReference(field.GetValue(component), Original.unitPrefab.transform, compass.unitPrefab.transform, seen, 0);
                }
                foreach (var source in Get<GameObject[]>(prefab.GetComponent<PowerSupply>(), "powerSources"))
                    if (source == null || source.GetComponent<IEngine>() == null)
                        throw new InvalidOperationException("Electrical power source is not connected to a jet.");
                if (Encyclopedia.Lookup[AircraftKey] != Variant || !Encyclopedia.i.aircraft.Contains(Variant))
                    throw new InvalidOperationException("Definition was not indexed.");
                Logger.LogInfo("A19J RUNTIME VALIDATION PASSED: registration, clone isolation, original damage parts, electrical power, nozzle references, no afterburners.");
                exitCode = 0;
            }
            catch (Exception ex) { Logger.LogError("A19J RUNTIME VALIDATION FAILED: " + ex); }
            SessionStopped();
            if (exitCode == 0 && Environment.GetEnvironmentVariable("A19J_VALIDATE_SPAWN") == "1")
            {
                yield return SpawnValidationMode();
                if (spawnValidationFailed) exitCode = 1;
            }
            // BepInEx buffers disk logging. Leave time for the final measurements
            // to flush before this test-only process exits.
            Debug.Log("A19J VALIDATION EXIT CODE: " + exitCode);
            yield return new WaitForSecondsRealtime(3f);
            Application.Quit(exitCode);
        }

        private IEnumerator SpawnValidationMode()
        {
            float deadline = Time.realtimeSinceStartup + 120f;
            MainMenu menu = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                menu = Resources.FindObjectsOfTypeAll<MainMenu>().FirstOrDefault(x => x.isActiveAndEnabled);
                if (menu != null) break;
                yield return null;
            }
            if (menu == null) { SpawnValidationError("Main menu was not ready."); yield break; }
            // In headless mode the visual loading-overlay completion can stall
            // MainMenu.State. Give the active scene time to finish loading instead.
            yield return new WaitForSecondsRealtime(5f);
            try
            {
                // This is the stock encyclopedia action: SocketType.Offline,
                // GameState.Encyclopedia. No lobby or remote socket is created.
                Logger.LogInfo("A19J spawn validation: opening the offline encyclopedia.");
                menu.SelectEncyclopedia();
            }
            catch (Exception ex) { SpawnValidationError(ex.ToString()); }
            if (spawnValidationFailed) yield break;
            EncyclopediaBrowser browser = null;
            deadline = Time.realtimeSinceStartup + 120f;
            while (Time.realtimeSinceStartup < deadline)
            {
                browser = Resources.FindObjectsOfTypeAll<EncyclopediaBrowser>().FirstOrDefault(x => x.isActiveAndEnabled && Get<bool>(x, "initialized"));
                if (browser != null) break;
                yield return null;
            }
            if (browser == null) { SpawnValidationError("Offline encyclopedia did not initialize."); yield break; }
            yield return new WaitForSecondsRealtime(2f);
            try
            {
                if (!IsOfflineSession()) throw new InvalidOperationException("Stock offline host was not recognised.");
                ValidateFactionInventory();
                NetworkConfigured(SocketType.Steam);
                var blocked = AccessTools.Method(typeof(Spawner), "SpawnAircraft").Invoke(Spawner.i, new object[]
                { null, Variant.unitPrefab, null, 1f, default(LiveryKey), default(GlobalPosition), Quaternion.identity, Vector3.zero, null, null, "A19J guard test", 1f, 0.5f });
                if (blocked != null) throw new InvalidOperationException("Patched spawner did not reject the online-mode probe.");
                NetworkConfigured(SocketType.Offline);
                Logger.LogInfo("A19J PATCHED SPAWN GUARD PASSED: online-mode probe rejected before instantiation without opening an online socket; actual offline local host accepted.");
            }
            catch (Exception ex) { SpawnValidationError(ex.ToString()); }
            if (spawnValidationFailed) yield break;
            try { AccessTools.Method(typeof(EncyclopediaBrowser), "RemoveUnit").Invoke(browser, null); }
            catch (Exception ex) { SpawnValidationError(ex.ToString()); }
            if (spawnValidationFailed) yield break;
            yield return null;
            try { AccessTools.Method(typeof(EncyclopediaBrowser), "SpawnAircraft").Invoke(browser, new object[] { Variant }); }
            catch (Exception ex) { SpawnValidationError(ex.ToString()); }
            if (spawnValidationFailed) yield break;
            yield return new WaitForSecondsRealtime(4f);
            try
            {
                var spawned = Get<GameObject>(browser, "spawnedUnitObject");
                if (spawned == null || !spawned.activeInHierarchy) throw new InvalidOperationException("A-19J did not spawn.");
                var aircraft = spawned.GetComponent<Aircraft>();
                if (aircraft == null || aircraft.definition != Variant) throw new InvalidOperationException("Spawned aircraft has the wrong definition.");
                if (aircraft.engineStates.Count != 2) throw new InvalidOperationException("Live aircraft did not initialize two engine states.");
                var liveJets = aircraft.engineStates.OfType<Turbojet>().ToArray();
                if (liveJets.Length != 2) throw new InvalidOperationException("Live engine states are not two Turbojets.");
                foreach (var jet in liveJets)
                {
                    if (Get<ControlInputs>(jet, "controlInputs") == null) throw new InvalidOperationException("Jet controls were not initialized.");
                    var nozzles = Get<JetNozzle[]>(jet, "nozzles");
                    if (nozzles.Length != 1) throw new InvalidOperationException("Live jet has the wrong nozzle count.");
                    foreach (var nozzle in nozzles)
                    {
                        var part = Get<UnitPart>(nozzle, "part");
                        if (part == null || part.parentUnit != aircraft || part.rb == null)
                            throw new InvalidOperationException("Nozzle did not initialize on the live aircraft physics part.");
                    }
                }
                Logger.LogInfo("A19J SPAWN VALIDATION PASSED: live offline encyclopedia aircraft, two initialized jet controls/states, attached nozzle physics and original airframe.");
            }
            catch (Exception ex) { SpawnValidationError(ex.ToString()); }
            if (!spawnValidationFailed)
                yield return RunupValidationMode(Get<GameObject>(browser, "spawnedUnitObject").GetComponent<Aircraft>());
        }

        private IEnumerator RunupValidationMode(Aircraft aircraft)
        {
            // Test-only restrained run-up in the offline encyclopedia. Normal
            // gameplay never enters this coroutine or changes these controls.
            var jets = aircraft.engineStates.OfType<Turbojet>().ToArray();
            float fuelBefore = 0f;
            try
            {
                if (jets.Length != 2) throw new InvalidOperationException("Run-up requires exactly two live jets.");
                var bodies = Get<List<UnitPart>>(aircraft, "partLookup").Where(x => x != null && x.rb != null).Select(x => x.rb).Distinct().ToArray();
                if (bodies.Length == 0) throw new InvalidOperationException("Run-up has no live rigidbodies to restrain.");
                foreach (var body in bodies)
                    body.constraints = RigidbodyConstraints.FreezeAll;
                // Refuel takes a fill ratio, not a mass in kilograms.
                foreach (var tank in aircraft.GetFuelTanks()) tank.Refuel(1f);
                fuelBefore = aircraft.GetFuelQuantity();
                if (fuelBefore <= 0f) throw new InvalidOperationException("Run-up test has no fuel.");
                foreach (var jet in jets)
                    foreach (var nozzle in Get<JetNozzle[]>(jet, "nozzles"))
                        if (Vector3.Dot(Get<Transform>(nozzle, "thrustTransform").forward, aircraft.transform.forward) < 0.99f)
                            throw new InvalidOperationException("Jet thrust is not aligned with the aircraft's forward axis.");
                Time.timeScale = 1f;
                Logger.LogInfo("A19J run-up validation: restrained offline engine start, full throttle for 20 seconds.");
            }
            catch (Exception ex) { SpawnValidationError(ex.ToString()); }
            if (spawnValidationFailed) yield break;
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                aircraft.Ignition = true;
                foreach (var jet in jets) Get<ControlInputs>(jet, "controlInputs").throttle = 1f;
                yield return null;
            }
            try
            {
                foreach (var jet in jets)
                {
                    // JetNozzle.GetTotalThrust() reports afterburner additions
                    // only. Read actual dry engine thrust and force sent to its nozzle.
                    float delivered = jet.GetThrust();
                    float nozzleForce = Get<JetNozzle[]>(jet, "nozzles").Sum(x => Get<float>(x, "totalThrust"));
                    if (float.IsNaN(delivered) || delivered < 20000f || jet.GetRPMRatio() < 0.95f)
                        throw new InvalidOperationException("Jet did not reach normal thrust/RPM: " + jet.name + ", thrust=" + delivered + ", RPM ratio=" + jet.GetRPMRatio());
                    if (Mathf.Abs(nozzleForce - delivered) > 10f)
                        throw new InvalidOperationException("Jet thrust did not reach its nozzle: " + jet.name + ", nozzle force=" + nozzleForce);
                    Logger.LogInfo("A19J run-up result: " + jet.name + " = " + delivered.ToString("F0") + " N, RPM ratio " + jet.GetRPMRatio().ToString("F3"));
                }
                float consumed = fuelBefore - aircraft.GetFuelQuantity();
                if (consumed <= 1f || consumed > 50f || float.IsNaN(consumed))
                    throw new InvalidOperationException("Run-up fuel draw was outside the expected range: " + consumed);
                Logger.LogInfo("A19J RUN-UP VALIDATION PASSED: two forward-facing jets spooled up and drew " + consumed.ToString("F2") + " kg of fuel. Restrained test, not a flight-handling test.");
            }
            catch (Exception ex) { SpawnValidationError(ex.ToString()); }
            aircraft.Ignition = false;
            foreach (var jet in jets) Get<ControlInputs>(jet, "controlInputs").throttle = 0f;
        }

        private void SpawnValidationError(string error)
        {
            spawnValidationFailed = true;
            Logger.LogError("A19J SPAWN VALIDATION FAILED: " + error);
        }

        private static void CheckForeignReference(object value, Transform original, Transform compass, HashSet<object> seen, int depth)
        {
            if (value == null || depth > 16) return;
            Transform transform = value is Component component ? component.transform : value is GameObject go ? go.transform : null;
            if (transform != null)
            {
                if (transform.IsChildOf(original) || transform.IsChildOf(compass))
                    throw new InvalidOperationException("Foreign aircraft reference: " + transform.name);
                return;
            }
            if (value is Object || value is string || value is Delegate || value.GetType().IsPrimitive || value.GetType().IsEnum) return;
            if (!value.GetType().IsValueType && !seen.Add(value)) return;
            if (value is IEnumerable values)
            {
                foreach (var item in values) CheckForeignReference(item, original, compass, seen, depth + 1);
                return;
            }
            if (!value.GetType().IsSerializable) return;
            foreach (var field in value.GetType().GetFields(Fields))
                if (!field.IsStatic && !field.IsNotSerialized && (field.IsPublic || field.IsDefined(typeof(SerializeField), true)))
                    CheckForeignReference(field.GetValue(value), original, compass, seen, depth + 1);
        }
    }
}
