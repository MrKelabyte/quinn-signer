using System;
using System.Collections.Generic;
using Features.Signing.Recognition;
using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// The end-of-race cinematic, run identically on every device.
    ///
    /// When the phase becomes Finale, every device lines the ships up in the same formation
    /// past the finish line (computed locally from the course, so nothing extra is sent), moves
    /// the camera to a shared shot, and plays laser/explosion effects when the server announces
    /// hits. The winner's taps are sent to the server, which decides what gets hit.
    ///
    /// Plain C# class ticked by RaceManager, so it needs no scene setup.
    /// </summary>
    public class RaceFinale
    {
        private class Fx
        {
            public GameObject Go;
            public float Age, Life;
            public Action<Fx, float> Update; // (fx, normalized age 0..1)
            public Vector3 Velocity;
        }

        private static readonly Color LaserColor = new Color(1f, 0.15f, 0.2f, 1f);
        private static readonly Color HitColor = new Color(1f, 0.6f, 0.1f, 1f);
        private static readonly Color BoomColor = new Color(1f, 0.85f, 0.3f, 1f);

        private readonly RaceManager _rm;
        private readonly List<Fx> _fx = new List<Fx>();
        private readonly HashSet<ulong> _destroyed = new HashSet<ulong>();
        private readonly Dictionary<ulong, int> _hits = new Dictionary<ulong, int>();
        private bool _active;
        private bool _gunToggle;
        private float _fireCooldown;
        private Vector3 _anchor, _forward, _right, _up;

        public bool Active => _active;
        public Vector3 CameraPosition { get; private set; }
        public Vector3 CameraLookAt { get; private set; }

        /// <summary>Seconds since the finale started (0 when inactive).</summary>
        public float Elapsed => _active ? (float)(_rm.Now - _rm.FinaleStartTime.Value) : 0f;

        public bool CanFire => _active && Elapsed >= _rm.FinaleIntroSeconds;

        /// <summary>Ships still standing (not counting the winner).</summary>
        public int TargetsRemaining
        {
            get
            {
                int n = 0;
                foreach (var id in _rm.Players)
                    if (id != _rm.WinnerClientId.Value && !_destroyed.Contains(id)) n++;
                return n;
            }
        }

        /// <summary>0..1 health of a target ship, as seen by this device.</summary>
        public float TargetHealth01(ulong id)
        {
            if (_destroyed.Contains(id)) return 0f;
            _hits.TryGetValue(id, out var h);
            return 1f - Mathf.Clamp01(h / (float)Mathf.Max(1, _rm.HitsToDestroy));
        }

        public RaceFinale(RaceManager rm)
        {
            _rm = rm;
            _rm.LaserFired += OnLaserFired;
            _rm.ShipDestroyed += OnShipDestroyed;
            NetworkRacePlayer.CamFrameReceived += OnCamFrame;
        }

        public void Dispose()
        {
            _rm.LaserFired -= OnLaserFired;
            _rm.ShipDestroyed -= OnShipDestroyed;
            NetworkRacePlayer.CamFrameReceived -= OnCamFrame;
            Reset();
        }

        /// <summary>Clear everything (ships visible again, effects removed).</summary>
        public void Reset()
        {
            _active = false;
            foreach (var p in NetworkRacePlayer.All.Values)
            {
                if (!p) continue;
                p.PoseOverridden = false;
                SetShipVisible(p, true);
            }
            _destroyed.Clear();
            _hits.Clear();
            foreach (var fx in _fx) if (fx.Go) UnityEngine.Object.Destroy(fx.Go);
            _fx.Clear();
        }

        public void Tick(float dt)
        {
            var phase = _rm.Phase.Value;

            if (phase == RacePhase.Finale && !_active) Begin();
            else if (phase != RacePhase.Finale && _active) End();

            // Ships blown up in the finale come back for the next race.
            if ((phase == RacePhase.Lobby || phase == RacePhase.Countdown) && _destroyed.Count > 0)
                Reset();

            if (_active)
            {
                LayoutShips(dt);
                HandleLocalFiring(dt);
                SendWinnerCam(dt);
            }

            TickFx(dt);
        }

        // ---------- begin / end ----------

        private void Begin()
        {
            _active = true;
            _destroyed.Clear();
            _hits.Clear();
            _hasRemoteCam = false;
            _camSendTimer = 0f;

            var course = _rm.Course;
            float end = course.Length;
            course.Frame(end, out _forward, out _right, out _up);
            _anchor = course.PointAt(end) + _forward * 30f;

            foreach (var p in NetworkRacePlayer.All.Values)
                if (p) p.PoseOverridden = true;
        }

        private void End()
        {
            _active = false;
            foreach (var p in NetworkRacePlayer.All.Values)
                if (p) p.PoseOverridden = false;
        }

        // ---------- formation + camera ----------

        private void LayoutShips(float dt)
        {
            ulong winnerId = _rm.WinnerClientId.Value;
            var losers = new List<NetworkRacePlayer>();
            NetworkRacePlayer winner = null;

            foreach (var id in _rm.Players)
            {
                if (!NetworkRacePlayer.All.TryGetValue(id, out var p) || !p) continue;
                if (id == winnerId) winner = p; else losers.Add(p);
            }

            float k = 1f - Mathf.Exp(-3f * dt);
            float t = Time.time;

            var winnerPos = _anchor - _forward * 12f;
            var losersCenter = _anchor + _forward * 22f + _up * 2f;

            // Losers line up ahead; remember which one is the current target (first one still alive,
            // matching the server's targeting order).
            Vector3? targetPos = null;
            for (int i = 0; i < losers.Count; i++)
            {
                float lateral = (i - (losers.Count - 1) * 0.5f) * 9f;
                var pos = losersCenter + _right * lateral + _up * Mathf.Sin(t * 3f + i) * 0.8f;
                var rot = Quaternion.LookRotation(_forward, _up) * Quaternion.Euler(0f, 0f, Mathf.Sin(t * 4f + i) * 15f);
                Place(losers[i], pos, rot, k);
                if (targetPos == null && !_destroyed.Contains(losers[i].OwnerClientId)) targetPos = pos;
            }

            // The winner turns to face whichever ship it is shooting at.
            if (winner)
            {
                var aimAt = targetPos ?? (losers.Count > 0 ? losersCenter : winnerPos + _forward * 10f);
                var aimRot = Quaternion.LookRotation(aimAt - winner.transform.position, _up);
                Place(winner, winnerPos + _up * Mathf.Sin(t * 2f) * 0.4f, aimRot, 1f - Mathf.Exp(-8f * dt));
            }

            // Shared camera shot: behind and to the side of the winner, looking at everyone.
            // Shared shot from behind the winner (almost on its axis) so both the winner and its
            // targets stay in frame even on narrow portrait phone screens.
            float aspect = UnityEngine.Camera.main ? UnityEngine.Camera.main.aspect : 16f / 9f;
            float back = aspect < 1f ? 24f : 16f; // portrait: pull back further
            var lookAt = losers.Count > 0 ? Vector3.Lerp(winnerPos, losersCenter, 0.4f) : winnerPos;
            CameraPosition = winnerPos - _forward * back + _up * 7f + _right * 2.5f;
            CameraLookAt = lookAt;
        }

        private static void Place(NetworkRacePlayer p, Vector3 pos, Quaternion rot, float k)
        {
            var tr = p.transform;
            tr.SetPositionAndRotation(Vector3.Lerp(tr.position, pos, k), Quaternion.Slerp(tr.rotation, rot, k));
        }

        // ---------- winner camera ----------

        private const int CamWidth = 192, CamHeight = 108;
        private const int CamJpegQuality = 40;
        private const int CamMaxBytes = 5000;      // stays under UnityTransport's 6144-byte payload limit
        private const float CamSendInterval = 0.2f; // ~5 frames per second

        private Texture2D _remoteCam, _sendTex;
        private bool _hasRemoteCam;
        private float _camSendTimer;

        /// <summary>The winner's camera for the "see who beat you" panel: live on the winner's own
        /// device, snapshots everywhere else. Null when there's nothing to show (e.g. PC winner).</summary>
        public Texture WinnerCamTexture
        {
            get
            {
                if (!_active) return null;
                var local = NetworkRacePlayer.Local;
                if (local && _rm.IsWinner(local.OwnerClientId))
                    return SlrtkSignRecognizer.IsReady ? SlrtkSignRecognizer.PreviewTexture : null;
                return _hasRemoteCam ? _remoteCam : null;
            }
        }

        public Rect WinnerCamUvRect => SlrtkSignRecognizer.ActivePreviewUvRect;

        private void SendWinnerCam(float dt)
        {
            var local = NetworkRacePlayer.Local;
            if (!local || !_rm.IsWinner(local.OwnerClientId) || _rm.Players.Count < 2) return;
            var src = SlrtkSignRecognizer.PreviewTexture;
            if (src == null || !SlrtkSignRecognizer.IsReady) return;

            _camSendTimer -= dt;
            if (_camSendTimer > 0f) return;
            _camSendTimer = CamSendInterval;

            try
            {
                // Shrink the camera image on the GPU, read it back, compress, send.
                var rt = RenderTexture.GetTemporary(CamWidth, CamHeight, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                if (_sendTex == null) _sendTex = new Texture2D(CamWidth, CamHeight, TextureFormat.RGB24, false);
                _sendTex.ReadPixels(new Rect(0, 0, CamWidth, CamHeight), 0, 0);
                _sendTex.Apply(false);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);

                var jpg = _sendTex.EncodeToJPG(CamJpegQuality);
                if (jpg.Length <= CamMaxBytes) local.WinnerCamFrameRpc(jpg);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RaceFinale] Winner cam frame failed: " + e.Message);
                _camSendTimer = 2f; // back off
            }
        }

        private void OnCamFrame(ulong senderId, byte[] jpg)
        {
            if (!_active || senderId != _rm.WinnerClientId.Value || jpg == null) return;
            if (_remoteCam == null) _remoteCam = new Texture2D(2, 2, TextureFormat.RGB24, false);
            _hasRemoteCam = _remoteCam.LoadImage(jpg);
        }

        // ---------- firing ----------

        private void HandleLocalFiring(float dt)
        {
            var local = NetworkRacePlayer.Local;
            if (!local || !_rm.IsWinner(local.OwnerClientId) || !CanFire) return;

            _fireCooldown -= dt;
            int taps = local.Controls != null ? local.Controls.TapsThisFrame : 0;
            if (Input.GetKeyDown(KeyCode.Space)) taps++;

            if (taps > 0 && _fireCooldown <= 0f)
            {
                _fireCooldown = 0.06f; // a little rate limit so mashing doesn't flood the network
                local.FireLaserRpc();
            }
        }

        private void OnLaserFired(ulong shooterId, ulong targetId)
        {
            if (!NetworkRacePlayer.All.TryGetValue(shooterId, out var shooter) || !shooter) return;
            if (!NetworkRacePlayer.All.TryGetValue(targetId, out var target) || !target) return;

            _hits.TryGetValue(targetId, out var h);
            _hits[targetId] = h + 1;

            _gunToggle = !_gunToggle;
            var from = shooter.transform.position + shooter.transform.forward * 2f + shooter.transform.right * (_gunToggle ? 1.2f : -1.2f);
            var to = target.transform.position + UnityEngine.Random.insideUnitSphere * 1.2f;

            SpawnLaser(from, to);
            SpawnFlash(to, HitColor, 2.5f, 0.2f);
            LocalPlayerCameraFollow.AddShake(0.15f);
        }

        private void OnShipDestroyed(ulong targetId)
        {
            _destroyed.Add(targetId);
            if (!NetworkRacePlayer.All.TryGetValue(targetId, out var target) || !target) return;

            var pos = target.transform.position;
            SetShipVisible(target, false);

            SpawnFlash(pos, BoomColor, 12f, 0.5f);
            SpawnDebris(pos, 28);
            LocalPlayerCameraFollow.AddShake(0.8f);
        }

        private static void SetShipVisible(NetworkRacePlayer p, bool visible)
        {
            if (p.shipVisual) p.shipVisual.gameObject.SetActive(visible);
            if (p.nameText) p.nameText.gameObject.SetActive(visible);
        }

        // ---------- effects ----------

        private void SpawnLaser(Vector3 from, Vector3 to)
        {
            var go = new GameObject("Laser");
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = RaceVisuals.Unlit(LaserColor);
            lr.positionCount = 2;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
            lr.useWorldSpace = true;
            lr.startWidth = 0.45f;
            lr.endWidth = 0.2f;

            _fx.Add(new Fx
            {
                Go = go, Life = 0.12f,
                Update = (fx, n) =>
                {
                    float w = Mathf.Lerp(0.45f, 0f, n);
                    lr.startWidth = w;
                    lr.endWidth = w * 0.5f;
                }
            });
        }

        private void SpawnFlash(Vector3 pos, Color color, float size, float life)
        {
            var go = RaceVisuals.Primitive(PrimitiveType.Sphere, RaceVisuals.Unlit(color), null);
            go.name = "Flash";
            go.transform.position = pos;

            _fx.Add(new Fx
            {
                Go = go, Life = life,
                Update = (fx, n) => fx.Go.transform.localScale = Vector3.one * (size * Mathf.Sin(n * Mathf.PI))
            });
        }

        private void SpawnDebris(Vector3 pos, int count)
        {
            var mats = new[] { RaceVisuals.Unlit(BoomColor), RaceVisuals.Unlit(HitColor), RaceVisuals.Lit(new Color(0.3f, 0.3f, 0.35f)) };
            for (int i = 0; i < count; i++)
            {
                var go = RaceVisuals.Primitive(PrimitiveType.Cube, mats[i % mats.Length], null);
                go.name = "Debris";
                go.transform.position = pos;
                go.transform.rotation = UnityEngine.Random.rotation;
                float scale = UnityEngine.Random.Range(0.3f, 1.1f);

                _fx.Add(new Fx
                {
                    Go = go,
                    Life = UnityEngine.Random.Range(1.0f, 1.8f),
                    Velocity = UnityEngine.Random.onUnitSphere * UnityEngine.Random.Range(8f, 22f),
                    Update = (fx, n) =>
                    {
                        fx.Go.transform.position += fx.Velocity * Time.deltaTime;
                        fx.Velocity *= 0.97f;
                        fx.Go.transform.Rotate(220f * Time.deltaTime, 160f * Time.deltaTime, 0f);
                        fx.Go.transform.localScale = Vector3.one * (scale * (1f - n));
                    }
                });
            }
        }

        private void TickFx(float dt)
        {
            for (int i = _fx.Count - 1; i >= 0; i--)
            {
                var fx = _fx[i];
                fx.Age += dt;
                if (!fx.Go || fx.Age >= fx.Life)
                {
                    if (fx.Go) UnityEngine.Object.Destroy(fx.Go);
                    _fx.RemoveAt(i);
                    continue;
                }
                fx.Update?.Invoke(fx, fx.Age / fx.Life);
            }
        }
    }
}
