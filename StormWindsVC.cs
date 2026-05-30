using Atmosphere;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WindAPI;

namespace StormWinds
{
    // =========================================================================
    //  StormWinds -- Wind provider for Kerbal Space Program 1.12.5
    //  Queries Volumetric Clouds data in 3D world space to inject turbulent
    //  storm-wind gusts scaled by local cloud density.
    // =========================================================================

    public class StormSettings
    {
        public float maxGustSpeed = 30.0f;
        public float densityThreshold = 0.2f;
        public float centerWeight = 0.7f;
        public float areaWeight = 0.3f;
        public float areaSampleRadius = 1000f;
        public float gustInterval = 7.0f;
        public float gustLerpSpeed = 1.0f;
        public float maxStormThickness = 6000f;
        public float groundWindFraction = 0.2f;
        public float fadeStartAlt = 0f;
        public float fadeEndAlt = 1000f;
        public bool debugMode = false;

        public StormSettings Clone()
        {
            return (StormSettings)this.MemberwiseClone();
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, once: false)]
    public class StormWindsController : MonoBehaviour, IWindProvider
    {
        // -----------------------------------------------------------------------
        // Configuration State
        // -----------------------------------------------------------------------
        private StormSettings defaultSettings = new StormSettings();
        private Dictionary<string, StormSettings> bodySettings = new Dictionary<string, StormSettings>(StringComparer.OrdinalIgnoreCase);

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

            Vessel v = FlightGlobals.ActiveVessel;
            float gustMag = 0f;
            StormSettings config = GetCurrentSettings(v.mainBody.bodyName);

            // Ensure vessel is > 1m ASL, body has atmosphere, and vessel is inside atmosphere
            bool inValidAtmosphere = v.altitude > 1.0f && v.mainBody.atmosphere && v.altitude < v.mainBody.atmosphereDepth;

            if (inValidAtmosphere)
            {
                if (_centerDensity > config.densityThreshold)
                {
                    gustMag = config.maxGustSpeed * (_centerDensity * config.centerWeight + _areaDensity * config.areaWeight);
                    gustMag = Mathf.Clamp(gustMag, 0f, config.maxGustSpeed);
                }

                // Altitude fadeout: scale winds down near the surface using radar altitude
                float altScale = 1f;
                if (config.fadeEndAlt > config.fadeStartAlt)
                {
                    float radarAlt = Mathf.Max(0f, (float)v.radarAltitude);
                    if (radarAlt <= config.fadeStartAlt)
                        altScale = config.groundWindFraction;
                    else if (radarAlt < config.fadeEndAlt)
                        altScale = Mathf.Lerp(config.groundWindFraction, 1f, (radarAlt - config.fadeStartAlt) / (config.fadeEndAlt - config.fadeStartAlt));
                    // above fadeEndAlt: altScale stays 1f
                }

                gustMag *= altScale;
            }

            _currentGustMag = gustMag;
            _gustTimer -= Time.fixedDeltaTime;

            if (_gustTimer <= 0f)
            {
                _gustTimer = config.gustInterval;
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

            _currentGust = Vector3.Lerp(_currentGust, _targetGust, Time.fixedDeltaTime * config.gustLerpSpeed);
            
            // -----------------------------------------------------------------------
            // Wind debug HUD -- HUD
            // -----------------------------------------------------------------------
            if (defaultSettings.debugMode)
            {
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
            Vector3[] sampleOffsets = new Vector3[8];

            while (true)
            {
                yield return new WaitForSeconds(1.0f);

                if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null)
                    continue;

                Vessel v = FlightGlobals.ActiveVessel;
                string bodyName = v.mainBody.bodyName;
                StormSettings config = GetCurrentSettings(bodyName);

                if (v.altitude <= 1.0f || !v.mainBody.atmosphere || v.altitude >= v.mainBody.atmosphereDepth)
                {
                    _centerDensity = 0f;
                    _areaDensity = 0f;
                    continue;
                }

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
                float radius = config.areaSampleRadius;
                sampleOffsets[0] = north * radius;
                sampleOffsets[1] = -north * radius;
                sampleOffsets[2] = east * radius;
                sampleOffsets[3] = -east * radius;
                sampleOffsets[4] = ne * radius;
                sampleOffsets[5] = nw * radius;
                sampleOffsets[6] = se * radius;
                sampleOffsets[7] = sw * radius;

                // Absolute maximum altitude to allow sampling for
                float atmMaxRadius = (float)v.mainBody.Radius + (float)v.mainBody.atmosphereDepth;

                var allClouds = CloudsManager.GetObjectList();
                if (allClouds != null)
                {
                    foreach (var layer in allClouds)
                    {
                        if (layer.Body == bodyName && layer.LayerRaymarchedVolume != null)
                        {
                            var volume = layer.LayerRaymarchedVolume;
                            float innerRadius = Mathf.Max(volume.PlanetRadius, volume.InnerSphereRadius);
                            float outerRadius = Mathf.Max(volume.PlanetRadius, volume.OuterSphereRadius);

                            // Skip entirely if this cloud starts in space
                            if (innerRadius >= atmMaxRadius)
                                continue;

                            // Clamp the upper sampling bounds
                            if (outerRadius > atmMaxRadius)
                                outerRadius = atmMaxRadius;

                            foundClouds = true;
                            Vector3 sphereCenter = volume.ParentTransform != null ? volume.ParentTransform.position : v.mainBody.transform.position;

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

                _centerDensity = Mathf.Clamp01(totalCenterMeters / config.maxStormThickness);
                _areaDensity = Mathf.Clamp01(totalAreaMeters / config.maxStormThickness);

                if (config.debugMode)
                {
                    Debug.Log($"[StormWinds] Body: {bodyName} | Alt: {v.altitude:F0} | CenterMeters: {totalCenterMeters:F0} | AreaMeters: {totalAreaMeters:F0} | GustTarget: {_currentGustMag:F1} m/s");
                }
            }
        }

        // -----------------------------------------------------------------------
        // Evaluates the vertical thickness (Optical Depth) of the cloud layer
        // -----------------------------------------------------------------------
        private float GetColumnThickness(CloudsRaymarchedVolume volume, Vector3 basePos, Vector3 up, Vector3 sphereCenter, float innerRadius, float outerRadius)
        {
            float distFromCenter = Vector3.Distance(basePos, sphereCenter);

            float startDist = Mathf.Max(0f, innerRadius - distFromCenter);
            float endDist = outerRadius - distFromCenter;
            float rayLength = endDist - startDist;

            if (rayLength <= 0f) return 0f;

            int steps = 10;
            float stepSize = rayLength / steps;
            float columnMeters = 0f;

            for (int i = 0; i < steps; i++)
            {
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
        // Config management
        // -----------------------------------------------------------------------
        private void LoadConfig()
        {
            ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("STORMWINDS_CONFIG");
            if (nodes == null || nodes.Length == 0)
            {
                Debug.LogWarning("[StormWinds] No STORMWINDS_CONFIG found. Using hardcoded defaults.");
                return;
            }

            // PASS 1: Identify Default Fallback configuration
            foreach (ConfigNode n in nodes)
            {
                if (!n.HasValue("bodyName") || n.GetValue("bodyName").Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    ParseSettingsNode(n, defaultSettings);
                    Debug.Log("[StormWinds] Loaded base Default settings.");
                }
            }

            // PASS 2: Load Body-Specific configurations overriding the default
            foreach (ConfigNode n in nodes)
            {
                if (n.HasValue("bodyName"))
                {
                    string rawNames = n.GetValue("bodyName");
                    if (rawNames.Trim().Equals("default", StringComparison.OrdinalIgnoreCase)) continue;

                    string[] bodies = rawNames.Split(',');
                    foreach (string body in bodies)
                    {
                        string bName = body.Trim();
                        if (!string.IsNullOrEmpty(bName))
                        {
                            StormSettings planetSettings = defaultSettings.Clone(); // Inherit unspecified vars from Default
                            ParseSettingsNode(n, planetSettings);
                            bodySettings[bName] = planetSettings;
                            Debug.Log($"[StormWinds] Loaded body-specific settings for: {bName}");
                        }
                    }
                }
            }
        }

        private void ParseSettingsNode(ConfigNode n, StormSettings s)
        {
            TryParseFloat(n, "maxGustSpeed", ref s.maxGustSpeed);
            TryParseFloat(n, "densityThreshold", ref s.densityThreshold);
            TryParseFloat(n, "centerWeight", ref s.centerWeight);
            TryParseFloat(n, "areaWeight", ref s.areaWeight);
            TryParseFloat(n, "areaSampleRadius", ref s.areaSampleRadius);
            TryParseFloat(n, "gustInterval", ref s.gustInterval);
            TryParseFloat(n, "gustLerpSpeed", ref s.gustLerpSpeed);
            TryParseFloat(n, "maxStormThickness", ref s.maxStormThickness);
            TryParseFloat(n, "groundWindFraction", ref s.groundWindFraction);
            TryParseFloat(n, "fadeStartAlt", ref s.fadeStartAlt);
            TryParseFloat(n, "fadeEndAlt", ref s.fadeEndAlt);
            TryParseBool(n, "enableDebug", ref s.debugMode);

            // Normalize weights
            float weightSum = s.centerWeight + s.areaWeight;
            if (weightSum > 0f)
            {
                s.centerWeight /= weightSum;
                s.areaWeight /= weightSum;
            }
        }

        private StormSettings GetCurrentSettings(string bodyName)
        {
            if (bodySettings.TryGetValue(bodyName, out StormSettings settings))
                return settings;

            return defaultSettings;
        }

        private static void TryParseFloat(ConfigNode n, string key, ref float field)
        {
            if (n.HasValue(key)) float.TryParse(n.GetValue(key), out field);
        }

        private static void TryParseBool(ConfigNode n, string key, ref bool field)
        {
            if (n.HasValue(key)) bool.TryParse(n.GetValue(key), out field);
        }
    }
}