// 행성 반지름 크기의 float 좌표 체인이 만드는 건물 모듈 어긋남(RSS에서 0.5~1 m)을 없애기 위해, PQSCity를 행성 부모에서 분리해 매 프레임 double로 구동하는 모드
using System;
using System.Reflection;
using UnityEngine;

namespace PQSCityPrecisionFix
{
    /// <summary>
    /// v0: drives only the home body's "KSC" PQSCity, only in the Space Center
    /// scene, and only on bodies large enough that float ULP at radius >= 0.25 m
    /// (stock-scale Kerbin is untouched). See design-detach-drive.md in the
    /// ksp-kk-precision project folder for the mechanism and measurements.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class SpaceCenterDriver : MonoBehaviour
    {
        // drive only when float ULP at planet radius reaches this (R >= 2^21 m)
        private const double minUlpToDrive = 0.25;
        // let PSystemSetup / PQSCity.Orientate settle first
        private const int attachFrame = 30;

        private PQSCity city;
        private Transform planetTransform;
        private Vector3d relPos;
        private QuaternionD relRot;
        private Vector3 savedLocalPos;
        private Quaternion savedLocalRot;
        private bool driving = false;
        private int frames = 0;

        public void LateUpdate()
        {
            if (!driving)
            {
                if (++frames < attachFrame)
                {
                    return;
                }
                TryAttach();
                if (!driving)
                {
                    return;
                }
            }
            Drive();
        }

        private void TryAttach()
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || home.pqsController == null)
            {
                Destroy(this);
                return;
            }

            double ulp = Math.Pow(2.0, Math.Floor(Math.Log(home.Radius, 2.0)) - 23.0);
            if (ulp < minUlpToDrive)
            {
                // stock-scale body: quantization is invisible, leave everything alone
                Destroy(this);
                return;
            }

            foreach (PQSCity candidate in UnityEngine.Object.FindObjectsOfType<PQSCity>())
            {
                if (candidate.name == "KSC" && candidate.sphere == home.pqsController)
                {
                    city = candidate;
                    break;
                }
            }
            if (city == null || city.transform.parent == null)
            {
                Debug.Log("[PQSCityPrecisionFix] no driveable KSC found, standing down");
                Destroy(this);
                return;
            }

            planetTransform = city.transform.parent;
            savedLocalPos = city.transform.localPosition;
            savedLocalRot = city.transform.localRotation;

            // Planet-relative pose, double where the game has it. The float
            // localPosition fallback only costs ROOT accuracy (uniform for the
            // whole campus); module-relative precision does not depend on it.
            relPos = (Vector3d)savedLocalPos;
            FieldInfo prpField = typeof(PQSCity).GetField("planetRelativePosition",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (prpField != null)
            {
                object value = prpField.GetValue(city);
                if (value is Vector3d && ((Vector3d)value).magnitude > 0.0)
                {
                    relPos = (Vector3d)value;
                }
            }
            relRot = savedLocalRot;

            // A detached child of a DontDestroyOnLoad parent falls into the
            // active scene and would be destroyed on scene unload — pin it.
            city.transform.SetParent(null, true);
            GameObject.DontDestroyOnLoad(city.gameObject);
            driving = true;
            Debug.Log("[PQSCityPrecisionFix] driving " + city.name + " on " + home.bodyName
                + " (ULP at radius = " + ulp + " m), relPos = " + relPos);
        }

        private void Drive()
        {
            if (city == null || planetTransform == null)
            {
                driving = false;
                return;
            }
            // Compose in double from the SAME float planet transform the terrain
            // quads use: root stays coherent with the terrain (status quo), while
            // the detached subtree below now contains no planet-scale floats.
            Vector3d planetPos = (Vector3d)planetTransform.position;
            QuaternionD planetRot = planetTransform.rotation;
            Vector3d world = planetPos + planetRot * relPos;
            city.transform.position = (Vector3)world;
            city.transform.rotation = (Quaternion)(planetRot * relRot);
        }

        public void OnDestroy()
        {
            // scene teardown: hand the stock hierarchy back exactly as captured,
            // so the next PQS mod re-scan sees the vanilla structure
            if (driving && city != null && planetTransform != null)
            {
                city.transform.SetParent(planetTransform, false);
                city.transform.localPosition = savedLocalPos;
                city.transform.localRotation = savedLocalRot;
                Debug.Log("[PQSCityPrecisionFix] reattached " + city.name);
            }
            driving = false;
        }
    }
}
