using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PQSCityPrecisionFix
{
    /// <summary>
    /// Drives every PQSCity on a body whose radius reaches 2^21 m (float ULP
    /// at radius >= 0.25 m; all stock bodies are below and left untouched), in
    /// the Space Center and Flight scenes. See README.md for the mechanism,
    /// the measurements, and the Kerbal Konstructs interop.
    ///
    /// Lifecycle rules, grounded in the KSP 1.12.5 decompile:
    /// - PQS re-scans its children into a cached mod array (SetupMods), and a
    ///   rebuild is synchronous — isAlive polling cannot observe it. A detached
    ///   city would drop out of the new array and lose its LOD callbacks, so
    ///   each entry snapshots the array reference and, when it changes,
    ///   reattaches and forces one more re-scan before re-detaching.
    /// - Whatever re-Orientates a PQSCity writes planet-relative values into
    ///   localPosition/localRotation, so OnPQSCityOrientated recaptures them
    ///   as the new relative pose (guarded: a detached city whose Orientate
    ///   skipped those writes must not adopt world values).
    /// - PQSCity2 is NOT driven: its positioning machine re-runs mid-scene
    ///   without an event, writes world values while detached, and can
    ///   retarget the floating origin from localPosition.
    /// - Detach/reattach steps the whole subtree by up to ~ULP(radius) in one
    ///   frame, so both are deferred while an unpacked vessel is nearby.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    [DefaultExecutionOrder(29000)]
    public class Driver : MonoBehaviour
    {
        // 2^21 m: the smallest radius whose float ULP reaches 0.25 m
        private const double minRadiusToDrive = 2097152.0;
        // detach while a freshly loaded vessel is still packed (colliders off)
        private const int scanFrame = 2;
        // pick up cities created mid-scene (Kerbal Konstructs spawns groups on the fly)
        private const int rescanIntervalFrames = 120;
        // no pose step while an unpacked vessel is this close (meters)
        private const float stepClearance = 10000f;

        private class Entry
        {
            public PQSCity city;
            public CelestialBody body;
            public PQS sphere;
            public Transform planet;
            public Transform tr;
            public Vector3d relPos;
            public QuaternionD relRot;
            public Vector3 savedLocalPos;
            public Quaternion savedLocalRot;
            public int savedSiblingIndex;
            public object modsAtDetach;
            public Vector3 lastPos;
            public Quaternion lastRot;
            public bool poseValid;
            public bool detached;
        }

        private static readonly FieldInfo cityPrp = typeof(PQSCity).GetField(
            "planetRelativePosition", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo pqsMods = typeof(PQS).GetField(
            "mods", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo pqsResetModList = typeof(PQS).GetMethod(
            "ResetModList", BindingFlags.Instance | BindingFlags.NonPublic);

        // Kerbal Konstructs interop, resolved once per game session. The group
        // selected in an OPEN KK group editor must stay attached: KK reads
        // localPosition back as a planet-relative position and saves it.
        private static bool kkChecked;
        private static bool kkArmed;
        private static FieldInfo kkEditorInstance;   // GroupEditor._instance (the lazy property would construct one)
        private static MethodInfo kkEditorIsOpen;    // KKWindow.IsOpen()
        private static FieldInfo kkSelectedGroup;    // GroupEditor.selectedGroup
        private static FieldInfo kkPqsCity;          // GroupCenter.pqsCity

        private List<Entry> entries;
        private readonly HashSet<int> considered = new HashSet<int>();
        private int framesBeforeScan;
        private int rescanCountdown;
        private bool subscribed;

        private void FixedUpdate()
        {
            DriveAll();
        }

        private void LateUpdate()
        {
            if (entries == null)
            {
                if (++framesBeforeScan < scanFrame)
                {
                    return;
                }
                Initialize();
                if (entries == null)
                {
                    return;
                }
            }
            if (--rescanCountdown <= 0)
            {
                rescanCountdown = rescanIntervalFrames;
                Rescan();
                InvalidatePoses();
            }
            Manage();
        }

        private void OnOriginShift(Vector3d offset, Vector3d offsetNonKrakensbane)
        {
            DriveAll();
        }

        private void Initialize()
        {
            if (FlightGlobals.fetch == null)
            {
                return;  // retry next frame
            }
            bool anyLargeBody = false;
            for (int i = FlightGlobals.Bodies.Count; i-- > 0;)
            {
                CelestialBody body = FlightGlobals.Bodies[i];
                if (body.pqsController != null && body.Radius >= minRadiusToDrive)
                {
                    anyLargeBody = true;
                    break;
                }
            }
            if (!anyLargeBody)
            {
                Debug.Log("[PQSCityPrecisionFix] no large-radius body, standing down");
                Destroy(gameObject);
                return;
            }
            EnsureKKReflection();
            if (pqsMods == null || pqsResetModList == null)
            {
                Debug.LogWarning("[PQSCityPrecisionFix] PQS mod-list tracking unavailable — update this mod");
            }
            entries = new List<Entry>();
            GameEvents.onFloatingOriginShift.Add(OnOriginShift);
            GameEvents.OnPQSCityOrientated.Add(OnCityOrientated);
            subscribed = true;
            Rescan();
            rescanCountdown = rescanIntervalFrames;
            Debug.Log("[PQSCityPrecisionFix] managing " + entries.Count + " cities");
        }

        private void Rescan()
        {
            foreach (PQSCity city in FindObjectsOfType<PQSCity>())
            {
                Consider(city);
            }
        }

        private void Consider(PQSCity city)
        {
            int id = city.GetInstanceID();
            if (considered.Contains(id))
            {
                return;
            }
            if (city.sphere == null || city.transform.parent == null)
            {
                return;  // mid-construction — retry on a later rescan
            }
            if (city.sphere.radius < minRadiusToDrive
                || city.transform.parent != city.sphere.transform)
            {
                considered.Add(id);
                return;
            }
            CelestialBody body = city.gameObject.GetComponentInParent<CelestialBody>();
            if (body == null)
            {
                return;
            }
            considered.Add(id);
            entries.Add(new Entry
            {
                city = city,
                body = body,
                sphere = city.sphere,
                planet = city.transform.parent,
                tr = city.transform,
            });
        }

        private static void EnsureKKReflection()
        {
            if (kkChecked)
            {
                return;
            }
            kkChecked = true;
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                if (loaded.name != "KerbalKonstructs")
                {
                    continue;
                }
                Type groupCenter = loaded.assembly.GetType("KerbalKonstructs.Core.GroupCenter");
                Type groupEditor = loaded.assembly.GetType("KerbalKonstructs.UI.GroupEditor");
                if (groupCenter != null && groupEditor != null)
                {
                    kkPqsCity = groupCenter.GetField("pqsCity",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    kkEditorInstance = groupEditor.GetField("_instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    kkEditorIsOpen = groupEditor.GetMethod("IsOpen",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    kkSelectedGroup = groupEditor.GetField("selectedGroup",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                kkArmed = kkPqsCity != null && kkEditorInstance != null
                    && kkEditorIsOpen != null && kkSelectedGroup != null;
                if (kkArmed)
                {
                    Debug.Log("[PQSCityPrecisionFix] Kerbal Konstructs detected, group-editor guard armed");
                }
                else
                {
                    Debug.LogWarning("[PQSCityPrecisionFix] Kerbal Konstructs detected but the"
                        + " group-editor guard is NOT resolvable — update this mod");
                }
                break;
            }
        }

        /// <summary>
        /// The PQSCity of the group selected in an OPEN KK group editor, or null.
        /// </summary>
        private static PQSCity CityUnderGroupEdit()
        {
            if (!kkArmed)
            {
                return null;
            }
            try
            {
                object group = kkSelectedGroup.GetValue(null);
                if (group == null)
                {
                    return null;
                }
                object editor = kkEditorInstance.GetValue(null);
                if (editor == null || !(bool)kkEditorIsOpen.Invoke(editor, null))
                {
                    return null;
                }
                return kkPqsCity.GetValue(group) as PQSCity;
            }
            catch (Exception e)
            {
                kkArmed = false;
                Debug.LogWarning("[PQSCityPrecisionFix] KK group-editor probe failed, guard disarmed: " + e.Message);
                return null;
            }
        }

        private void Manage()
        {
            PQSCity edited = CityUnderGroupEdit();
            List<PQS> resetNeeded = null;
            for (int i = entries.Count; i-- > 0;)
            {
                Entry entry = entries[i];
                if (entry.city == null)
                {
                    entries.RemoveAt(i);  // destroyed (e.g. KK group deletion) — nothing left to restore
                    continue;
                }
                try
                {
                    if (ReferenceEquals(entry.city, edited))
                    {
                        if (entry.detached)
                        {
                            Reattach(entry);
                        }
                        continue;
                    }
                    bool sphereAlive = entry.sphere != null && entry.sphere.isAlive;
                    if (!entry.detached)
                    {
                        if (sphereAlive && SafeToStep(entry))
                        {
                            Detach(entry);
                        }
                    }
                    else if (!sphereAlive)
                    {
                        Reattach(entry);
                    }
                    else if (ModListStale(entry) && SafeToStep(entry))
                    {
                        // a mid-scene SetupMods ran while we were detached and
                        // dropped this city — restore, re-scan, re-detach next frame
                        Reattach(entry);
                        if (resetNeeded == null)
                        {
                            resetNeeded = new List<PQS>();
                        }
                        if (!resetNeeded.Contains(entry.sphere))
                        {
                            resetNeeded.Add(entry.sphere);
                        }
                    }
                    else
                    {
                        Drive(entry);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PQSCityPrecisionFix] dropping " + entry.city.name + ": " + e.Message);
                    TrySalvage(entry);
                    entries.RemoveAt(i);
                }
            }
            if (resetNeeded != null && pqsResetModList != null)
            {
                for (int i = resetNeeded.Count; i-- > 0;)
                {
                    try
                    {
                        pqsResetModList.Invoke(resetNeeded[i], null);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[PQSCityPrecisionFix] ResetModList failed: " + e.Message);
                    }
                }
            }
        }

        private static bool SafeToStep(Entry entry)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || vessel.packed)
            {
                return true;
            }
            return (vessel.transform.position - entry.tr.position).sqrMagnitude
                > stepClearance * stepClearance;
        }

        private static bool ModListStale(Entry entry)
        {
            return pqsMods != null
                && !ReferenceEquals(pqsMods.GetValue(entry.sphere), entry.modsAtDetach);
        }

        private void Detach(Entry entry)
        {
            entry.savedLocalPos = entry.tr.localPosition;
            entry.savedLocalRot = entry.tr.localRotation;
            entry.savedSiblingIndex = entry.tr.GetSiblingIndex();
            Vector3d prp;
            entry.relPos = TryReadPlanetRelativePosition(entry.city, out prp)
                ? prp
                : (Vector3d)entry.tr.localPosition;
            entry.relRot = entry.savedLocalRot;

            // A detached child of a DontDestroyOnLoad parent falls into the
            // active scene and would be destroyed on scene unload — pin it.
            entry.tr.SetParent(null, true);
            DontDestroyOnLoad(entry.city.gameObject);
            entry.modsAtDetach = pqsMods != null ? pqsMods.GetValue(entry.sphere) : null;
            entry.poseValid = false;
            entry.detached = true;

            Drive(entry);
            UpdateSpawnPoints(entry);
        }

        private static void Reattach(Entry entry)
        {
            entry.tr.SetParent(entry.planet, false);
            entry.tr.localPosition = entry.savedLocalPos;
            entry.tr.localRotation = entry.savedLocalRot;
            entry.tr.SetSiblingIndex(entry.savedSiblingIndex);
            entry.poseValid = false;
            entry.detached = false;
        }

        private static void TrySalvage(Entry entry)
        {
            try
            {
                if (entry.detached && entry.city != null && entry.planet != null)
                {
                    Reattach(entry);
                }
            }
            catch (Exception)
            {
                // the entry is being dropped either way
            }
        }

        private static bool TryReadPlanetRelativePosition(PQSCity city, out Vector3d value)
        {
            if (cityPrp != null)
            {
                object boxed = cityPrp.GetValue(city);
                if (boxed is Vector3d && ((Vector3d)boxed).magnitude > 0.0)
                {
                    value = (Vector3d)boxed;
                    return true;
                }
            }
            value = Vector3d.zero;
            return false;
        }

        private void DriveAll()
        {
            if (entries == null)
            {
                return;
            }
            Transform lastPlanet = null;
            Vector3d planetPos = Vector3d.zero;
            QuaternionD planetRot = QuaternionD.identity;
            for (int i = entries.Count; i-- > 0;)
            {
                Entry entry = entries[i];
                if (!entry.detached || entry.city == null || entry.planet == null)
                {
                    continue;
                }
                if (!ReferenceEquals(entry.planet, lastPlanet))
                {
                    lastPlanet = entry.planet;
                    planetPos = (Vector3d)entry.planet.position;
                    planetRot = entry.planet.rotation;
                }
                Drive(entry, planetPos, planetRot);
            }
        }

        private static void Drive(Entry entry)
        {
            Drive(entry, (Vector3d)entry.planet.position, entry.planet.rotation);
        }

        private static void Drive(Entry entry, Vector3d planetPos, QuaternionD planetRot)
        {
            // Compose in double from the SAME float planet transform the terrain
            // quads use: root stays coherent with the terrain, while the detached
            // subtree below contains no planet-scale floats.
            Vector3 position = (Vector3)(planetPos + planetRot * entry.relPos);
            Quaternion rotation = (Quaternion)(planetRot * entry.relRot);
            // exact compare: rewriting an identical pose still dirties the whole
            // subtree (and its colliders), and Unity's == is approximate
            if (entry.poseValid
                && position.x == entry.lastPos.x && position.y == entry.lastPos.y && position.z == entry.lastPos.z
                && rotation.x == entry.lastRot.x && rotation.y == entry.lastRot.y
                && rotation.z == entry.lastRot.z && rotation.w == entry.lastRot.w)
            {
                return;
            }
            entry.tr.position = position;
            entry.tr.rotation = rotation;
            entry.lastPos = position;
            entry.lastRot = rotation;
            entry.poseValid = true;
        }

        private void InvalidatePoses()
        {
            // bounded staleness for the identical-pose skip: if someone wrote the
            // transform behind our back, we re-assert within one rescan interval
            for (int i = entries.Count; i-- > 0;)
            {
                entries[i].poseValid = false;
            }
        }

        /// <summary>
        /// Mirrors the tail of PQSCity.Orientate() against the corrected world
        /// pose: vessels spawn from these lat/lon/alt values.
        /// </summary>
        private static void UpdateSpawnPoints(Entry entry)
        {
            PQSCity city = entry.city;
            Vector3d world = (Vector3d)entry.planet.position
                + (QuaternionD)entry.planet.rotation * entry.relPos;
            entry.body.GetLatLonAlt(world, out city.lat, out city.lon, out city.alt);
            if (city.launchSite != null && city.launchSite.launchSiteTransform != null)
            {
                city.launchSite.SetSpawnPointsLatLonAlt();
            }
            if (city.spaceCenterFacility != null && city.spaceCenterFacility.facilityTransform != null)
            {
                city.spaceCenterFacility.SetSpawnPointsLatLonAlt();
            }
        }

        /// <summary>
        /// Someone re-Orientated a city (KSCSwitcher, KK editor). Orientate
        /// writes planet-relative values, so recapture them as the new pose —
        /// except the writes it may have skipped, which on a detached city
        /// would hand us world values.
        /// </summary>
        private void OnCityOrientated(CelestialBody body, string cityName)
        {
            for (int i = entries.Count; i-- > 0;)
            {
                Entry entry = entries[i];
                if (entry.city == null || entry.body != body
                    || entry.city.gameObject.name != cityName)
                {
                    continue;
                }
                Vector3d prp;
                if (TryReadPlanetRelativePosition(entry.city, out prp))
                {
                    entry.relPos = prp;
                }
                else if (!entry.detached)
                {
                    entry.relPos = (Vector3d)entry.tr.localPosition;
                }
                if (!entry.detached || entry.city.reorientToSphere)
                {
                    entry.relRot = entry.tr.localRotation;
                }
                entry.savedLocalPos = (Vector3)entry.relPos;
                entry.savedLocalRot = (Quaternion)entry.relRot;
                entry.poseValid = false;
                if (entry.detached)
                {
                    Drive(entry);
                    UpdateSpawnPoints(entry);
                }
            }
        }

        private void OnDestroy()
        {
            if (subscribed)
            {
                GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
                GameEvents.OnPQSCityOrientated.Remove(OnCityOrientated);
                subscribed = false;
            }
            if (entries == null)
            {
                return;
            }
            // scene teardown: hand the stock hierarchy back exactly as captured
            for (int i = entries.Count; i-- > 0;)
            {
                Entry entry = entries[i];
                if (entry.detached && entry.city != null && entry.planet != null)
                {
                    try
                    {
                        Reattach(entry);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[PQSCityPrecisionFix] reattach failed for "
                            + entry.city.name + ": " + e.Message);
                    }
                }
            }
            entries = null;
        }
    }
}
