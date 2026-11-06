using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
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
        public const string Version = "0.1.1";
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
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private void Awake()
        {
            instance = this;
            harmony = new Harmony(Guid);
            harmony.Patch(AccessTools.Method(typeof(Hangar), "Awake"), postfix: new HarmonyMethod(typeof(A19JPlugin), nameof(HangarAwake)));
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
            if (Variant != null) AddToLoadedHangars();
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

                // Append the new definition/index. Existing network definition
                // indices are not reordered or rewritten.
                ((INetworkDefinition)definition).LookupIndex = encyclopedia.IndexLookup.Count;
                encyclopedia.IndexLookup.Add(definition);
                encyclopedia.aircraft.Add(definition);
                Encyclopedia.Lookup.Add(AircraftKey, definition);
                Set(encyclopedia, "aircraftAndVehiclesCache", null);
                Variant = definition;
                AddToLoadedHangars();
                Logger.LogInfo("A19J BUILD CHECK PASSED: separate A-19J registered; 2 jets, no propfans, original A-19 unchanged.");
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
        private static void AddToHangar(Hangar hangar)
        {
            if (Variant == null || Original == null) return;
            var list = Get<AircraftDefinition[]>(hangar, "availableAircraft");
            if (list == null || !list.Contains(Original) || list.Contains(Variant)) return;
            Set(hangar, "availableAircraft", list.Concat(new[] { Variant }).ToArray());
        }
        private static void AddToLoadedHangars()
        {
            foreach (var hangar in Resources.FindObjectsOfTypeAll<Hangar>()) AddToHangar(hangar);
        }

        private IEnumerator ValidationMode()
        {
            float deadline = Time.realtimeSinceStartup + 180f;
            while (Variant == null && Time.realtimeSinceStartup < deadline) yield return null;
            int exitCode = 1;
            try
            {
                if (Variant == null) throw new InvalidOperationException("A-19J did not finish registration.");
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
