using Atmosphere;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WindAPI;
using static PQSCity2;

namespace StormWinds
{
    // =========================================================================
    //  StormWinds -- Wind provider for Kerbal Space Program 1.12.5
    //  Queries Volumetric Clouds data in 3D world space to inject turbulent
    //  storm-wind gusts scaled by local cloud density.
    // =========================================================================

    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class StormWindsController : MonoBehaviour, IWindProvider
    {
        // -----------------------------------------------------------------------
        // Configuration
        // -----------------------------------------------------------------------
        private float maxGustSpeed = 40f;
        private float densityThreshold = 0.2f;
        private float centerWeight = 0.6f;
        private float areaWeight = 0.4f;
        private float areaSampleRadius = 1000f;
        private float gustInterval = 3.0f;
        private float gustLerpSpeed = 2.0f;
        private float maxStormThickness = 4000f;
        private bool debugMode = false;

        // -----------------------------------------------------------------------
        // Gust state
        // -----------------------------------------------------------------------
        private Vector3 _currentGust = Vector3.zero;
        private Vector3 _targetGust = Vector3.zero;
        private float _gustTimer = 0f;

        private float _centerDensity = 0f;
        private float _areaDensity = 0f;
        private float _currentGustMag = 0f;

        // -----------------------------------------------------------------------
        // Timing
        // -----------------------------------------------------------------------
        private float _msgTimer = 0f;
        private const float MSG_INTERVAL = 1.5f;
        private Coroutine cloudSamplingRoutine;

        // -----------------------------------------------------------------------
        // IWindProvider
        // -----------------------------------------------------------------------
        public string ProviderID => "StormWinds";

        public Vector3 GetWind(CelestialBody body, Part part, Vector3 position)
        {
            if (body == null || _currentGust == Vector3.zero) return Vector3.zero;

            if (part != null && FlightGlobals.ActiveVessel != null)
            {
                if (part.vessel == FlightGlobals.ActiveVessel)
                    return _currentGust;
            }

            if (FlightGlobals.ActiveVessel != null)
            {
                float distSqr = (float)(position - FlightGlobals.ActiveVessel.GetWorldPos3D()).sqrMagnitude;
                if (distSqr < 4000000f) // 2km radius
                    return _currentGust;
            }

            return Vector3.zero;
        }

        // -----------------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------------
        public void Start()
        {
            LoadConfig();
            StartCoroutine(RegisterWithAPI());
            cloudSamplingRoutine = StartCoroutine(SampleCloudsRoutine());
        }

        private IEnumerator RegisterWithAPI()
        {
            int maxAttempts = 20; // 10 seconds total (20 * 0.5s)
            int attempts = 0;

            while (WindManager.Instance == null && attempts < maxAttempts)
            {
                attempts++;
                yield return new WaitForSeconds(0.5f);
            }

            if (WindManager.Instance != null)
            {
                WindManager.Instance.RegisterProvider(this);
                Debug.Log("[StormWinds] Registered with WindAPI. Native volumetric sampling active.");
            }
            else
            {
                Debug.LogWarning("[StormWinds] Failed to find WindAPI after 10 seconds. StormWinds will deactivate.");
            }
        }

        public void OnDestroy()
        {
            if (WindManager.Instance != null)
                WindManager.Instance.DeregisterProvider(this);

            if (cloudSamplingRoutine != null)
                StopCoroutine(cloudSamplingRoutine);
        }

        // -----------------------------------------------------------------------
        // Physics update
        // -----------------------------------------------------------------------
        public void FixedUpdate()
        {
            if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null)
            {
                _currentGust = Vector3.zero;
                _targetGust = Vector3.zero;
                return;
            }

            float gustMag = 0f;

            if (_centerDensity > densityThreshold)
            {
                gustMag = maxGustSpeed * (_centerDensity * centerWeight + _areaDensity * areaWeight);
                gustMag = Mathf.Clamp(gustMag, 0f, maxGustSpeed);
            }

            _currentGustMag = gustMag;
            _gustTimer -= Time.fixedDeltaTime;

            if (_gustTimer <= 0f)
            {
                _gustTimer = gustInterval;
                _targetGust = (gustMag > 0f) ? RandomHorizontalGust(gustMag) : Vector3.zero;
            }
            else if (gustMag <= 0f)
            {
                _targetGust = Vector3.zero;
            }
            else
            {
                if (_targetGust != Vector3.zero)
                    _targetGust = _targetGust.normalized * gustMag;
            }

            _currentGust = Vector3.Lerp(_currentGust, _targetGust, Time.fixedDeltaTime * gustLerpSpeed);
        }

        // -----------------------------------------------------------------------
        // Per-frame update -- HUD
        // -----------------------------------------------------------------------
        public void Update()
        {
            if (FlightGlobals.ActiveVessel == null) return;

            _msgTimer += Time.deltaTime;
            if (_msgTimer >= MSG_INTERVAL)
            {
                _msgTimer = 0f;
                if (_currentGustMag > 0.5f)
                {
                    ScreenMessages.PostScreenMessage(
                        $"[Weather] {_currentGustMag:F1} m/s",
                        MSG_INTERVAL + 0.1f,
                        ScreenMessageStyle.UPPER_CENTER);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Gust direction
        // -----------------------------------------------------------------------
        private Vector3 RandomHorizontalGust(float magnitude)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return Vector3.zero;

            Vector3 up = (v.transform.position - v.mainBody.transform.position).normalized;
            float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            Vector3 north = Vector3.ProjectOnPlane(Vector3.up, up).normalized;

            if (north.sqrMagnitude < 0.001f)
                north = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;

            Vector3 east = Vector3.Cross(up, north).normalized;

            return (Mathf.Cos(angle) * north + Mathf.Sin(angle) * east).normalized * magnitude;
        }

        // -----------------------------------------------------------------------
        // Volumetric Cloud Sampling Coroutine
        // Runs every 1 second to preserve performance
        // -----------------------------------------------------------------------
        private IEnumerator SampleCloudsRoutine()
        {
            // Cache the offsets array outside the loop to prevent GC allocation
            Vector3[] sampleOffsets = new Vector3[8];

            while (true)
            {
                yield return new WaitForSeconds(1.0f);

                if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null)
                    continue;

                Vessel v = FlightGlobals.ActiveVessel;
                string bodyName = v.mainBody.bodyName;
                Vector3 craftPos = v.transform.position;

                float totalCenterMeters = 0f;
                float totalAreaMeters = 0f;
                bool foundClouds = false;

                // Setup Up vector and directional vectors
                Vector3 up = (craftPos - v.mainBody.transform.position).normalized;
                Vector3 north = Vector3.ProjectOnPlane(Vector3.up, up).normalized;
                if (north.sqrMagnitude < 0.001f)
                    north = Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
                Vector3 east = Vector3.Cross(up, north).normalized;

                Vector3 ne = (north + east).normalized;
                Vector3 nw = (north - east).normalized;
                Vector3 se = (-north + east).normalized;
                Vector3 sw = (-north - east).normalized;

                // Update the pre-allocated array (Zero Garbage)
                sampleOffsets[0] = north * areaSampleRadius;
                sampleOffsets[1] = -north * areaSampleRadius;
                sampleOffsets[2] = east * areaSampleRadius;
                sampleOffsets[3] = -east * areaSampleRadius;
                sampleOffsets[4] = ne * areaSampleRadius;
                sampleOffsets[5] = nw * areaSampleRadius;
                sampleOffsets[6] = se * areaSampleRadius;
                sampleOffsets[7] = sw * areaSampleRadius;

                // Iterate via standard foreach to avoid LINQ GC allocations
                var allClouds = CloudsManager.GetObjectList();
                if (allClouds != null)
                {
                    foreach (var layer in allClouds)
                    {
                        if (layer.Body == bodyName && layer.LayerRaymarchedVolume != null)
                        {
                            foundClouds = true;
                            var volume = layer.LayerRaymarchedVolume;
                            Vector3 sphereCenter = volume.ParentTransform != null ? volume.ParentTransform.position : v.mainBody.transform.position;

                            float outerRadius = Mathf.Max(volume.PlanetRadius, volume.OuterSphereRadius);
                            float innerRadius = Mathf.Max(volume.PlanetRadius, volume.InnerSphereRadius);

                            // 1. Center sample
                            totalCenterMeters += GetColumnThickness(volume, craftPos, up, sphereCenter, innerRadius, outerRadius);

                            // 2. Area samples
                            float layerAreaSum = 0f;
                            for (int i = 0; i < sampleOffsets.Length; i++)
                            {
                                layerAreaSum += GetColumnThickness(volume, craftPos + sampleOffsets[i], up, sphereCenter, innerRadius, outerRadius);
                            }
                            totalAreaMeters += (layerAreaSum / sampleOffsets.Length);
                        }
                    }
                }

                if (!foundClouds)
                {
                    _centerDensity = 0f;
                    _areaDensity = 0f;
                    continue;
                }

                _centerDensity = Mathf.Clamp01(totalCenterMeters / maxStormThickness);
                _areaDensity = Mathf.Clamp01(totalAreaMeters / maxStormThickness);

                if (debugMode)
                {
                    Debug.Log($"[StormWinds] Alt: {v.altitude:F0} | CenterMeters: {totalCenterMeters:F0} | AreaMeters: {totalAreaMeters:F0} | Gust: {_currentGustMag:F1} m/s");
                }
            }
        }

        // -----------------------------------------------------------------------
        // Evaluates the vertical thickness (Optical Depth) of the cloud layer
        // -----------------------------------------------------------------------
        private float GetColumnThickness(CloudsRaymarchedVolume volume, Vector3 basePos, Vector3 up, Vector3 sphereCenter, float innerRadius, float outerRadius)
        {
            float distFromCenter = Vector3.Distance(basePos, sphereCenter);

            // Calculate how far up we need to trace to traverse the entire cloud layer.
            // If we are below the clouds, startDist evaluates to the distance to the cloud base.
            // If we are inside the clouds, startDist is 0 and we evaluate to the cloud tops.
            float startDist = Mathf.Max(0f, innerRadius - distFromCenter);
            float endDist = outerRadius - distFromCenter;
            float rayLength = endDist - startDist;

            // If rayLength <= 0, the vessel is flying above the storm tops. No wind!
            if (rayLength <= 0f) return 0f;

            int steps = 10;
            float stepSize = rayLength / steps;
            float columnMeters = 0f;

            for (int i = 0; i < steps; i++)
            {
                // Sample at the midpoint of each ray segment
                float d = startDist + (i + 0.5f) * stepSize;
                Vector3 samplePos = basePos + up * d;

                float coverage = volume.SampleCoverage(samplePos, out float _, false);
                if (coverage > 0f)
                {
                    columnMeters += coverage * stepSize;
                }
            }

            return columnMeters;
        }

        // -----------------------------------------------------------------------
        // Config loader
        // -----------------------------------------------------------------------
        private void LoadConfig()
        {
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("STORMWINDS_CONFIG");
            if (nodes == null || nodes.Length == 0)
            {
                Debug.LogWarning("[StormWinds] No STORMWINDS_CONFIG found. Using default settings.");
                return;
            }

            ConfigNode n = nodes[0];
            TryParseFloat(n, "maxGustSpeed", ref maxGustSpeed);
            TryParseFloat(n, "densityThreshold", ref densityThreshold);
            TryParseFloat(n, "centerWeight", ref centerWeight);
            TryParseFloat(n, "areaWeight", ref areaWeight);
            TryParseFloat(n, "areaSampleRadius", ref areaSampleRadius);
            TryParseFloat(n, "gustInterval", ref gustInterval);
            TryParseFloat(n, "gustLerpSpeed", ref gustLerpSpeed);
            TryParseFloat(n, "maxStormThickness", ref maxStormThickness);

            if (n.HasValue("enableDebug"))
                bool.TryParse(n.GetValue("enableDebug"), out debugMode);

            // Normalize weights
            float weightSum = centerWeight + areaWeight;
            if (weightSum > 0f)
            {
                centerWeight /= weightSum;
                areaWeight /= weightSum;
            }

            Debug.Log($"[StormWinds] Config loaded. MaxGust={maxGustSpeed}, Threshold={densityThreshold}, Radius={areaSampleRadius}m");
        }

        private static void TryParseFloat(ConfigNode n, string key, ref float field)
        {
            if (n.HasValue(key)) float.TryParse(n.GetValue(key), out field);
        }
    }
}