// 행성 반지름 크기의 float 좌표 체인이 만드는 건물 모듈 어긋남(RSS에서 0.5~1 m)을 없애기 위해, PQSCity/PQSCity2를 행성 부모에서 분리해 매 프레임 double로 구동하는 모드
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PQSCityPrecisionFix
{
    /// <summary>
    /// Drives every PQSCity/PQSCity2 that sits on a body large enough that
    /// float ULP at radius >= 0.25 m (stock-scale bodies are untouched), in the
    /// Space Center and Flight scenes. See design-detach-drive.md in the
    /// ksp-kk-precision project folder for the mechanism and measurements.
    ///
    /// Lifecycle rules (decompile-grounded):
    /// - A city is detached only while its sphere.isAlive, and reattached when
    ///   the sphere dies: PQS re-scans children into its cached mod array on
    ///   sphere starts, so the stock hierarchy must be in place by then.
    /// - The drive runs in FixedUpdate (very late execution order, after
    ///   Krakensbane/FloatingOrigin), again in LateUpdate for rendering, and on
    ///   the floating-origin-shift event.
    /// - When some other code re-Orientates a detached city (KSCSwitcher, KK
    ///   editor, scenery re-snap), the values it writes into localPosition/
    ///   localRotation are computed from planet-relative inputs, so we
    ///   recapture them as the new relative pose on OnPQSCityOrientated and
    ///   refresh the launch-site/facility spawn points from the corrected
    ///   world pose.
    /// - PQSCity2 re-snaps its surface position on OnScenerySettingChanged and
    ///   may call FloatingOrigin.SetOffset from its localPosition while doing
    ///   so — detached that would teleport the origin — so on that event every
    ///   detached PQSCity2 is reattached first; it re-detaches automatically
    ///   once PositioningCompleted is true again.
    ///
    /// Kerbal Konstructs interop (reflection, no hard dependency):
    /// KK group centers ARE PQSCity instances, and driving them is what makes
    /// KK statics and the KK editor camera smooth (KK points the FlightCamera
    /// at the selected static's transform; on the stock planet-parented chain
    /// that pivot moves in >= 0.25 m quanta, juddering the whole view). The
    /// one incompatible flow is KK's GROUP editor, which reads
    /// transform.localPosition back as a planet-relative position while moving
    /// a whole group and saves it into configs — so the group currently
    /// selected in an open GroupEditor window is temporarily reattached (stock
    /// behavior while group-editing), and re-detaches when the editor closes.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    [DefaultExecutionOrder(29000)]
    public class Driver : MonoBehaviour
    {
        // drive only when float ULP at planet radius reaches this (R >= 2^21 m)
        private const double minUlpToDrive = 0.25;
        // detach while a freshly loaded vessel is still packed (colliders off)
        private const int scanFrame = 2;
        // pick up cities created mid-scene (Kerbal Konstructs spawns groups on the fly)
        private const int rescanIntervalFrames = 120;

        private class Entry
        {
            public PQSSurfaceObject city;
            public CelestialBody body;
            public Transform planet;
            public Vector3d relPos;
            public QuaternionD relRot;
            public Vector3 savedLocalPos;
            public Quaternion savedLocalRot;
            public bool detached;
        }

        private static readonly FieldInfo cityPrp = typeof(PQSCity).GetField(
            "planetRelativePosition", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo city2Prp = typeof(PQSCity2).GetField(
            "planetRelativePosition", BindingFlags.Instance | BindingFlags.NonPublic);

        // Kerbal Konstructs reflection surface (resolved once, absent = inert)
        private static bool kkChecked = false;
        private static FieldInfo kkPqsCity = null;           // GroupCenter.pqsCity
        private static PropertyInfo kkGroupEditorInstance = null; // GroupEditor.instance
        private static MethodInfo kkGroupEditorIsOpen = null;     // KKWindow.IsOpen()
        private static FieldInfo kkSelectedGroup = null;          // GroupEditor.selectedGroup

        private List<Entry> entries = null;
        private readonly HashSet<int> processed = new HashSet<int>();
        private int frames = 0;
        private int rescanCountdown = 0;
        private bool subscribed = false;

        public void FixedUpdate()
        {
            if (entries != null)
            {
                DriveAll();
            }
        }

        public void LateUpdate()
        {
            if (entries == null)
            {
                if (++frames < scanFrame)
                {
                    return;
                }
                Scan();
                if (entries == null)
                {
                    return;
                }
            }
            if (--rescanCountdown <= 0)
            {
                rescanCountdown = rescanIntervalFrames;
                Rescan();
            }
            Manage();
        }

        private static double UlpAt(double radius)
        {
            return Math.Pow(2.0, Math.Floor(Math.Log(radius, 2.0)) - 23.0);
        }

        private void Scan()
        {
            bool anyLargeBody = false;
            for (int i = FlightGlobals.Bodies.Count; i-- > 0;)
            {
                CelestialBody body = FlightGlobals.Bodies[i];
                if (body.pqsController != null && UlpAt(body.Radius) >= minUlpToDrive)
                {
                    anyLargeBody = true;
                    break;
                }
            }
            if (!anyLargeBody)
            {
                // stock-scale system: quantization is invisible everywhere,
                // and no mod can grow a planet mid-scene — stand down for good
                Debug.Log("[PQSCityPrecisionFix] no large-radius body, standing down");
                Destroy(this);
                return;
            }
            EnsureKKReflection();
            entries = new List<Entry>();
            GameEvents.onFloatingOriginShift.Add(OnOriginShift);
            GameEvents.OnPQSCityOrientated.Add(OnCityOrientated);
            GameEvents.OnScenerySettingChanged.Add(OnScenerySettingChanged);
            subscribed = true;
            Rescan();
            Debug.Log("[PQSCityPrecisionFix] managing " + entries.Count + " cities");
        }

        private void Rescan()
        {
            foreach (PQSCity city in UnityEngine.Object.FindObjectsOfType<PQSCity>())
            {
                Consider(city);
            }
            foreach (PQSCity2 city in UnityEngine.Object.FindObjectsOfType<PQSCity2>())
            {
                Consider(city);
            }
        }

        private void Consider(PQSSurfaceObject city)
        {
            int id = city.GetInstanceID();
            if (processed.Contains(id))
            {
                return;
            }
            if (city.sphere == null || city.transform.parent == null)
            {
                // transient (mid-construction) — retry on a later rescan
                return;
            }
            if (UlpAt(city.sphere.radius) < minUlpToDrive)
            {
                processed.Add(id);
                return;
            }
            CelestialBody body = city.gameObject.GetComponentInParent<CelestialBody>();
            if (body == null)
            {
                return;
            }
            processed.Add(id);
            entries.Add(new Entry
            {
                city = city,
                body = body,
                planet = city.transform.parent,
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
                if (groupCenter != null)
                {
                    kkPqsCity = groupCenter.GetField("pqsCity",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (groupEditor != null)
                {
                    kkGroupEditorInstance = groupEditor.GetProperty("instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    kkGroupEditorIsOpen = groupEditor.GetMethod("IsOpen",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    kkSelectedGroup = groupEditor.GetField("selectedGroup",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                bool resolved = kkPqsCity != null && kkGroupEditorInstance != null
                    && kkGroupEditorIsOpen != null && kkSelectedGroup != null;
                Debug.Log("[PQSCityPrecisionFix] Kerbal Konstructs detected, group-editor guard "
                    + (resolved ? "armed" : "NOT resolvable — update this mod (group editing may misbehave)"));
                break;
            }
        }

        /// <summary>
        /// The PQSCity of the group currently selected in an OPEN KK group
        /// editor, or null. That one group must stay attached (stock behavior)
        /// while it is being group-edited.
        /// </summary>
        private static PQSSurfaceObject CityUnderGroupEdit()
        {
            if (kkGroupEditorInstance == null || kkGroupEditorIsOpen == null
                || kkSelectedGroup == null || kkPqsCity == null)
            {
                return null;
            }
            try
            {
                object editor = kkGroupEditorInstance.GetValue(null, null);
                if (editor == null || !(bool)kkGroupEditorIsOpen.Invoke(editor, null))
                {
                    return null;
                }
                object group = kkSelectedGroup.GetValue(null);
                if (group == null)
                {
                    return null;
                }
                return kkPqsCity.GetValue(group) as PQSSurfaceObject;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PQSCityPrecisionFix] KK group-editor probe failed: " + e.Message);
                return null;
            }
        }

        private void Manage()
        {
            PQSSurfaceObject editedCity = CityUnderGroupEdit();
            for (int i = entries.Count; i-- > 0;)
            {
                Entry entry = entries[i];
                if (entry.city == null)
                {
                    entries.RemoveAt(i);
                    continue;
                }
                if (ReferenceEquals(entry.city, editedCity))
                {
                    // KK group editor works on this group: stock hierarchy only
                    if (entry.detached)
                    {
                        Reattach(entry);
                    }
                    continue;
                }
                bool sphereAlive = entry.city.sphere != null && entry.city.sphere.isAlive;
                if (!entry.detached)
                {
                    if (sphereAlive && ReadyToDetach(entry))
                    {
                        Detach(entry);
                    }
                }
                else if (!sphereAlive)
                {
                    Reattach(entry);
                }
                else
                {
                    Drive(entry);
                }
            }
        }

        private static bool ReadyToDetach(Entry entry)
        {
            PQSCity2 city2 = entry.city as PQSCity2;
            if (city2 != null)
            {
                // its positioning machine may still move it (and may retarget
                // the floating origin from localPosition) — wait it out
                return city2.PositioningCompleted;
            }
            return true;
        }

        private void Detach(Entry entry)
        {
            entry.savedLocalPos = entry.city.transform.localPosition;
            entry.savedLocalRot = entry.city.transform.localRotation;
            entry.relPos = ReadPlanetRelativePosition(entry);
            entry.relRot = entry.savedLocalRot;

            // A detached child of a DontDestroyOnLoad parent falls into the
            // active scene and would be destroyed on scene unload — pin it.
            entry.city.transform.SetParent(null, true);
            GameObject.DontDestroyOnLoad(entry.city.gameObject);
            entry.detached = true;

            Drive(entry);
            UpdateSpawnPoints(entry);
        }

        private void Reattach(Entry entry)
        {
            entry.city.transform.SetParent(entry.planet, false);
            entry.city.transform.localPosition = entry.savedLocalPos;
            entry.city.transform.localRotation = entry.savedLocalRot;
            entry.detached = false;
        }

        private static Vector3d ReadPlanetRelativePosition(Entry entry)
        {
            FieldInfo field = (entry.city is PQSCity2) ? city2Prp : cityPrp;
            if (field != null)
            {
                object value = field.GetValue(entry.city);
                if (value is Vector3d && ((Vector3d)value).magnitude > 0.0)
                {
                    return (Vector3d)value;
                }
            }
            // float fallback only costs ROOT accuracy (uniform for the whole
            // subtree); module-relative precision does not depend on it
            return (Vector3d)entry.city.transform.localPosition;
        }

        private void DriveAll()
        {
            for (int i = entries.Count; i-- > 0;)
            {
                Entry entry = entries[i];
                if (entry.detached && entry.city != null)
                {
                    Drive(entry);
                }
            }
        }

        private static void Drive(Entry entry)
        {
            // Compose in double from the SAME float planet transform the terrain
            // quads use: root stays coherent with the terrain (status quo), while
            // the detached subtree below now contains no planet-scale floats.
            Vector3d planetPos = (Vector3d)entry.planet.position;
            QuaternionD planetRot = entry.planet.rotation;
            Vector3d world = planetPos + planetRot * entry.relPos;
            entry.city.transform.position = (Vector3)world;
            entry.city.transform.rotation = (Quaternion)(planetRot * entry.relRot);
        }

        /// <summary>
        /// Mirrors the tail of PQSCity.Orientate() against the corrected world
        /// pose: vessels spawn from these lat/lon/alt values.
        /// </summary>
        private static void UpdateSpawnPoints(Entry entry)
        {
            Vector3 position = entry.city.transform.position;
            PQSCity city = entry.city as PQSCity;
            if (city != null)
            {
                entry.body.GetLatLonAlt(position, out city.lat, out city.lon, out city.alt);
                if (city.launchSite != null && city.launchSite.launchSiteTransform != null)
                {
                    city.launchSite.SetSpawnPointsLatLonAlt();
                }
                if (city.spaceCenterFacility != null && city.spaceCenterFacility.facilityTransform != null)
                {
                    city.spaceCenterFacility.SetSpawnPointsLatLonAlt();
                }
                return;
            }
            PQSCity2 city2 = entry.city as PQSCity2;
            if (city2 != null)
            {
                entry.body.GetLatLonAlt(position, out city2.lat, out city2.lon, out city2.alt);
                if (city2.launchSite != null && city2.launchSite.launchSiteTransform != null)
                {
                    city2.launchSite.SetSpawnPointsLatLonAlt();
                }
                if (city2.spaceCenterFacility != null && city2.spaceCenterFacility.facilityTransform != null)
                {
                    city2.spaceCenterFacility.SetSpawnPointsLatLonAlt();
                }
            }
        }

        private void OnOriginShift(Vector3d offset, Vector3d offsetNonKrakensbane)
        {
            if (entries != null)
            {
                DriveAll();
            }
        }

        /// <summary>
        /// Someone re-Orientated a city (KSCSwitcher, KK editor, scenery
        /// re-snap). The values written into localPosition/localRotation are
        /// computed from planet-relative inputs (repositionRadial / lat+lon),
        /// so they ARE the new relative pose even while detached — recapture
        /// and re-place immediately.
        /// </summary>
        private void OnCityOrientated(CelestialBody body, string cityName)
        {
            if (entries == null)
            {
                return;
            }
            for (int i = entries.Count; i-- > 0;)
            {
                Entry entry = entries[i];
                if (entry.city == null || entry.body != body
                    || entry.city.gameObject.name != cityName)
                {
                    continue;
                }
                entry.relPos = ReadPlanetRelativePosition(entry);
                entry.relRot = entry.city.transform.localRotation;
                entry.savedLocalPos = (Vector3)entry.relPos;
                entry.savedLocalRot = entry.city.transform.localRotation;
                if (entry.detached)
                {
                    Drive(entry);
                    UpdateSpawnPoints(entry);
                }
            }
        }

        /// <summary>
        /// PQSCity2 re-runs its positioning machine after this event and may
        /// call FloatingOrigin.SetOffset from its localPosition — reattach
        /// first; Manage() re-detaches once PositioningCompleted again.
        /// </summary>
        private void OnScenerySettingChanged()
        {
            if (entries == null)
            {
                return;
            }
            for (int i = entries.Count; i-- > 0;)
            {
                Entry entry = entries[i];
                if (entry.city is PQSCity2 && entry.detached)
                {
                    Reattach(entry);
                }
            }
        }

        public void OnDestroy()
        {
            if (subscribed)
            {
                GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
                GameEvents.OnPQSCityOrientated.Remove(OnCityOrientated);
                GameEvents.OnScenerySettingChanged.Remove(OnScenerySettingChanged);
                subscribed = false;
            }
            // scene teardown: hand the stock hierarchy back exactly as captured,
            // so the next PQS mod re-scan sees the vanilla structure
            if (entries == null)
            {
                return;
            }
            for (int i = entries.Count; i-- > 0;)
            {
                Entry entry = entries[i];
                if (entry.detached && entry.city != null && entry.planet != null)
                {
                    Reattach(entry);
                }
            }
            entries = null;
        }
    }
}
