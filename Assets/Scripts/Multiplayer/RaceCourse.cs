using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Multiplayer
{
    [System.Serializable]
    public class CourseSettings
    {
        [Tooltip("Approximate race length in meters.")]
        public float length = 4000f;

        [Tooltip("Distance between the course's bend points. Smaller = twistier.")]
        public float controlPointSpacing = 90f;

        [Tooltip("How far the course swings left/right.")]
        public float lateralAmplitude = 45f;

        [Tooltip("How far the course swings up/down.")]
        public float verticalAmplitude = 18f;

        [Tooltip("How far from the center line you can fly before the wall pushes you back.")]
        public float tubeRadius = 16f;

        [Tooltip("Distance between glowing ring gates.")]
        public float gateSpacing = 45f;

        [Tooltip("Number of floating rocks around the course (sense of speed).")]
        public int debrisCount = 350;

        [Tooltip("Where Sign Zones start, as a fraction of the course (0..1). Four, evenly spaced.")]
        public float[] signZoneStarts = { 0.2f, 0.4f, 0.6f, 0.8f };
    }

    /// <summary>
    /// The race track. Generated from a seed, so every player builds the identical course
    /// locally and only the seed has to travel over the network.
    ///
    /// The course is a smooth curve (Catmull-Rom spline) sampled every ~2 m. "Progress" is
    /// how far along that curve a ship is, which drives standings, sign zones and the finish.
    /// </summary>
    public class RaceCourse
    {
        public const float StartGridDistance = 6f;

        private static readonly Color GateA = new Color(0.1f, 0.9f, 1f, 0.9f);
        private static readonly Color GateB = new Color(1f, 0.25f, 0.85f, 0.9f);
        private static readonly Color ZoneColor = new Color(0.65f, 0.3f, 1f, 1f);
        private static readonly Color FinishColor = new Color(1f, 0.85f, 0.15f, 1f);
        private static readonly Color StartColor = new Color(0.3f, 1f, 0.4f, 1f);

        public CourseSettings Settings { get; }
        public int Seed { get; }
        public float Length => _dist[_dist.Length - 1];
        public float TubeRadius => Settings.tubeRadius;
        public IReadOnlyList<float> SignZoneDistances => _zoneDistances;

        private readonly Vector3[] _points;   // dense samples along the center line
        private readonly Vector3[] _tangents;
        private readonly float[] _dist;       // distance from start at each sample
        private readonly List<float> _zoneDistances = new List<float>();
        private readonly GameObject _root;

        public RaceCourse(CourseSettings settings, int seed)
        {
            Settings = settings;
            Seed = seed;

            var control = BuildControlPoints(settings, seed);
            BuildSamples(control, out _points, out _tangents, out _dist);

            foreach (var f in settings.signZoneStarts)
                _zoneDistances.Add(Mathf.Clamp01(f) * Length);

            _root = new GameObject($"RaceCourse (seed {seed})");
            BuildVisuals(seed);
        }

        public void Destroy()
        {
            if (_root) Object.Destroy(_root);
        }

        // ---------- queries ----------

        public Vector3 PointAt(float distance) => Sample(_points, distance);

        public Vector3 TangentAt(float distance) => Sample(_tangents, distance).normalized;

        /// <summary>Distance along the course of the point closest to <paramref name="position"/>.</summary>
        /// <param name="hint">Last known sample index; makes the search local and cheap. Updated in place.</param>
        public float GetProgress(Vector3 position, ref int hint)
        {
            hint = Mathf.Clamp(hint, 0, _points.Length - 1);
            int best = FindClosest(position, Mathf.Max(0, hint - 30), Mathf.Min(_points.Length - 1, hint + 60));

            // Lost track of the ship (teleport, respawn): do a full search once.
            if ((_points[best] - position).sqrMagnitude > Settings.tubeRadius * Settings.tubeRadius * 9f)
                best = FindClosest(position, 0, _points.Length - 1);

            hint = best;

            // Refine between this sample and the next by projecting onto the segment.
            int next = Mathf.Min(best + 1, _points.Length - 1);
            var seg = _points[next] - _points[best];
            float segLen = seg.magnitude;
            if (segLen < 0.0001f) return _dist[best];
            float t = Mathf.Clamp(Vector3.Dot(position - _points[best], seg / segLen), 0f, segLen);
            return _dist[best] + t;
        }

        /// <summary>Starting grid slot for a player (index = join order).</summary>
        public Pose GridSlot(int index)
        {
            var center = PointAt(StartGridDistance);
            var forward = TangentAt(StartGridDistance);
            var right = Vector3.Cross(Vector3.up, forward).normalized;
            float lateral = ((index % 4) - 1.5f) * 5f;
            float back = (index / 4) * 6f;
            return new Pose(center + right * lateral - forward * back, Quaternion.LookRotation(forward, Vector3.up));
        }

        /// <summary>Right/up axes perpendicular to the course at a distance.</summary>
        public void Frame(float distance, out Vector3 forward, out Vector3 right, out Vector3 up)
        {
            forward = TangentAt(distance);
            right = Vector3.Cross(Vector3.up, forward).normalized;
            up = Vector3.Cross(forward, right);
        }

        // ---------- generation ----------

        private static List<Vector3> BuildControlPoints(CourseSettings s, int seed)
        {
            var rng = new System.Random(seed);
            float ox = (float)rng.NextDouble() * 1000f;
            float oy = (float)rng.NextDouble() * 1000f;

            int count = Mathf.Max(6, Mathf.CeilToInt(s.length / s.controlPointSpacing) + 1);
            var pts = new List<Vector3>(count);
            for (int k = 0; k < count; k++)
            {
                // Straight at the start and end, twisty in between.
                float ramp = Mathf.Clamp01((k - 1) / 2f) * Mathf.Clamp01((count - 2 - k) / 2f);
                float x = (Mathf.PerlinNoise(ox, k * 0.35f) * 2f - 1f) * s.lateralAmplitude * ramp;
                float y = (Mathf.PerlinNoise(oy, k * 0.3f) * 2f - 1f) * s.verticalAmplitude * ramp;
                pts.Add(new Vector3(x, y, k * s.controlPointSpacing));
            }
            return pts;
        }

        private static void BuildSamples(List<Vector3> cp, out Vector3[] points, out Vector3[] tangents, out float[] dist)
        {
            const int stepsPerSegment = 45;
            var list = new List<Vector3>();

            for (int i = 0; i < cp.Count - 1; i++)
            {
                var p0 = cp[Mathf.Max(i - 1, 0)];
                var p1 = cp[i];
                var p2 = cp[i + 1];
                var p3 = cp[Mathf.Min(i + 2, cp.Count - 1)];
                for (int s = 0; s < stepsPerSegment; s++)
                    list.Add(CatmullRom(p0, p1, p2, p3, s / (float)stepsPerSegment));
            }
            list.Add(cp[cp.Count - 1]);

            points = list.ToArray();
            tangents = new Vector3[points.Length];
            dist = new float[points.Length];

            for (int i = 0; i < points.Length; i++)
            {
                var a = points[Mathf.Max(i - 1, 0)];
                var b = points[Mathf.Min(i + 1, points.Length - 1)];
                tangents[i] = (b - a).normalized;
                if (i > 0) dist[i] = dist[i - 1] + Vector3.Distance(points[i - 1], points[i]);
            }
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private int FindClosest(Vector3 position, int from, int to)
        {
            int best = from;
            float bestSqr = float.MaxValue;
            for (int i = from; i <= to; i++)
            {
                float d = (_points[i] - position).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = i; }
            }
            return best;
        }

        private Vector3 Sample(Vector3[] arr, float distance)
        {
            if (distance <= 0f) return arr[0];
            if (distance >= Length) return arr[arr.Length - 1];

            // Binary search for the segment containing this distance.
            int lo = 0, hi = _dist.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (_dist[mid] <= distance) lo = mid; else hi = mid;
            }
            float span = _dist[hi] - _dist[lo];
            float t = span > 0.0001f ? (distance - _dist[lo]) / span : 0f;
            return Vector3.LerpUnclamped(arr[lo], arr[hi], t);
        }

        // ---------- visuals ----------

        private void BuildVisuals(int seed)
        {
            var parent = _root.transform;
            float r = Settings.tubeRadius;

            // Regular gates.
            var gateMesh = RaceVisuals.Torus(r, 0.35f);
            int n = 0;
            for (float d = Settings.gateSpacing; d < Length - Settings.gateSpacing * 0.5f; d += Settings.gateSpacing, n++)
            {
                if (IsNearZone(d)) continue;
                PlaceGate("Gate", gateMesh, RaceVisuals.Unlit(n % 2 == 0 ? GateA : GateB), d, parent);
            }

            // Start, finish and sign-zone gates are thicker and labelled.
            var bigMesh = RaceVisuals.Torus(r, 0.7f);
            PlaceGate("StartGate", bigMesh, RaceVisuals.Unlit(StartColor), StartGridDistance + 14f, parent);
            PlaceLabel("GO!", StartColor, StartGridDistance + 14f, parent);

            PlaceGate("FinishGate", bigMesh, RaceVisuals.Unlit(FinishColor), Length - 1f, parent);
            PlaceGate("FinishGate2", bigMesh, RaceVisuals.Unlit(Color.white), Length - 4f, parent);
            PlaceLabel("FINISH", FinishColor, Length - 1f, parent);

            foreach (var z in _zoneDistances)
            {
                PlaceGate("SignZoneGate", bigMesh, RaceVisuals.Unlit(ZoneColor), z, parent);
                PlaceGate("SignZoneGate", gateMesh, RaceVisuals.Unlit(ZoneColor), z + 6f, parent);
                PlaceLabel("SIGN ZONE", ZoneColor, z, parent);
            }

            // Floating rocks outside the tube give a sense of speed.
            var rng = new System.Random(seed * 31 + 7);
            var rockMat = RaceVisuals.Lit(new Color(0.35f, 0.38f, 0.5f));
            var rockMat2 = RaceVisuals.Lit(new Color(0.5f, 0.4f, 0.55f));
            for (int i = 0; i < Settings.debrisCount; i++)
            {
                float d = (float)rng.NextDouble() * Length;
                Frame(d, out _, out var right, out var up);
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float radius = r + 6f + (float)rng.NextDouble() * 45f;
                var pos = PointAt(d) + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius;

                var rock = RaceVisuals.Primitive(i % 3 == 0 ? PrimitiveType.Sphere : PrimitiveType.Cube, i % 2 == 0 ? rockMat : rockMat2, parent);
                rock.name = "Rock";
                rock.transform.position = pos;
                rock.transform.rotation = Quaternion.Euler((float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 360f, 0f);
                rock.transform.localScale = Vector3.one * (1f + (float)rng.NextDouble() * 5f);
            }
        }

        private bool IsNearZone(float d)
        {
            foreach (var z in _zoneDistances)
                if (Mathf.Abs(d - z) < 10f) return true;
            return false;
        }

        private void PlaceGate(string name, Mesh mesh, Material mat, float d, Transform parent)
        {
            var go = RaceVisuals.MeshObject(name, mesh, mat, parent);
            go.transform.SetPositionAndRotation(PointAt(d), Quaternion.LookRotation(TangentAt(d), Vector3.up));
        }

        private void PlaceLabel(string text, Color color, float d, Transform parent)
        {
            var go = new GameObject($"Label_{text}");
            go.transform.SetParent(parent, false);
            Frame(d, out var forward, out _, out var up);
            go.transform.SetPositionAndRotation(PointAt(d) + up * (Settings.tubeRadius + 3.5f), Quaternion.LookRotation(forward, Vector3.up));

            var tmp = go.AddComponent<TextMeshPro>();
            if (UiFactory.Font) tmp.font = UiFactory.Font;
            tmp.text = text;
            tmp.fontSize = 36;
            tmp.color = color;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.rectTransform.sizeDelta = new Vector2(60f, 10f);
        }
    }
}
