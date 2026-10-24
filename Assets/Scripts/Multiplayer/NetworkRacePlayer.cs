using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using TMPro;

namespace Multiplayer
{
    /// <summary>
    /// One racer's ship.
    ///
    /// Networking model: each player's own device flies its own ship (owner authority) and
    /// publishes its pose through owner-writable NetworkVariables. Other devices smoothly follow
    /// that pose. This keeps steering instant on each phone, which matters more for feel than
    /// anti-cheat does in a side-by-side party game. The server still owns the race rules
    /// (phases, finish order, winner, lasers) in <see cref="RaceManager"/>.
    /// </summary>
    public class NetworkRacePlayer : NetworkBehaviour
    {
        private static readonly Dictionary<ulong, NetworkRacePlayer> s_byClient = new Dictionary<ulong, NetworkRacePlayer>();

        /// <summary>Every spawned racer, by owner client id.</summary>
        public static IReadOnlyDictionary<ulong, NetworkRacePlayer> All => s_byClient;

        /// <summary>The ship this device controls (null until spawned).</summary>
        public static NetworkRacePlayer Local { get; private set; }

        public static event Action<NetworkRacePlayer> LocalSpawned;

        [Header("Flight")]
        [SerializeField] private float baseSpeed = 42f;
        [SerializeField] private float acceleration = 22f;
        [SerializeField] private float yawRate = 85f;
        [SerializeField] private float pitchRate = 60f;
        [SerializeField] private float maxPitch = 40f;
        [Tooltip("How quickly the ship eases back toward the track direction when you're not steering.")]
        [SerializeField] private float steerAssist = 1.2f;
        [SerializeField] private float bankAngle = 35f;
        [Tooltip("Speed multiplier while auto-piloting (answering a Sign Zone, or after finishing).")]
        [SerializeField] private float autopilotSpeedFactor = 0.6f;
        [SerializeField] private float remoteSmoothing = 12f;

        [Header("Sign Zone Boost")]
        [Tooltip("Extra speed per correct answer (0.25 = +25% each).")]
        [SerializeField] private float boostPerCorrect = 0.25f;
        [SerializeField] private float boostSecondsBase = 3f;
        [SerializeField] private float boostSecondsPerCorrect = 2f;
        [Tooltip("Speed multiplier when you get every answer in a zone wrong.")]
        [SerializeField] private float allWrongSlowdown = 0.85f;

        [Header("Ship Model")]
        [Tooltip("Rotation applied to the ship model so its nose points along the flight direction (+Z). " +
                 "The Arwing model's nose points to -Z, so it needs Y = 180. This overrides the rotation set on the model in the prefab.")]
        [SerializeField] private Vector3 modelRotation = new Vector3(0f, 180f, 0f);

        [Tooltip("Shift the model so its visual center sits on the ship's pivot. The model's mesh is off-center, " +
                 "which pushed the ship out of frame on narrow (phone) screens.")]
        [SerializeField] private bool centerModelOnPivot = true;

        [Header("References")]
        public Transform shipVisual;
        public Transform cameraFollowTarget;
        public TMP_Text nameText;
        [Tooltip("Legacy swipe input from the old arrow race. Disabled at runtime; kept so the prefab reference stays valid.")]
        public SwipeInputReader swipeInputReader;

        public Transform CameraFollowTarget => cameraFollowTarget != null ? cameraFollowTarget : transform;

        // ---- Pose (written by the owner, read by everyone) ----
        private readonly NetworkVariable<Vector3> _netPosition = new(Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<Quaternion> _netRotation = new(Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _netSpeed = new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> _netRoll = new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // ---- Race state / stats (owner) ----
        public NetworkVariable<float> Progress = new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public NetworkVariable<float> FinishTime = new(-1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public NetworkVariable<int> SignsCorrect = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public NetworkVariable<int> SignsAnswered = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public NetworkVariable<float> TopSpeed = new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        public NetworkVariable<int> BoostsEarned = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // ---- Server-owned ----
        public NetworkVariable<int> LasersFired = new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public NetworkVariable<FixedString64Bytes> PlayerName = new("Player", NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Raised on the local ship when it reaches a Sign Zone (argument = zone index).</summary>
        public event Action<int> SignZoneEntered;

        /// <summary>Raised on the local ship when it scrapes the course wall.</summary>
        public event Action WallBumped;

        /// <summary>Raised on the local ship when a boost starts (argument = multiplier).</summary>
        public event Action<float> BoostStarted;

        public float Speed => IsOwner ? _speed : _netSpeed.Value;
        public bool HasFinished => FinishTime.Value >= 0f;
        public bool IsAutopilot => _autopilot;
        public float BoostMultiplier => Time.time < _boostUntil ? _boostMultiplier : 1f;
        public float BaseSpeed => baseSpeed;
        public FlightInput Controls => _input;

        /// <summary>When true, the ship is positioned by the finale cinematic instead of flight/network.</summary>
        public bool PoseOverridden { get; set; }

        private FlightInput _input;
        private float _speed;
        private float _yaw, _pitch, _roll;
        private float _boostMultiplier = 1f, _boostUntil;
        private bool _autopilot;
        private bool _touchingWall;
        private int _progressHint;
        private int _nextZone;
        private RacePhase _lastPhase = (RacePhase)255;
        private Quaternion _visualBaseRotation = Quaternion.identity;
        private Vector3 _pivotToCenter;
        private bool _centerModel;

        // ---------- lifecycle ----------

        private void Awake()
        {
            if (!shipVisual) return;

            _visualBaseRotation = Quaternion.Euler(modelRotation);
            shipVisual.localRotation = _visualBaseRotation;

            if (centerModelOnPivot && TryGetModelBounds(out var worldCenter))
            {
                // Offset from the model's pivot to its visual center, in the model's own rotated space,
                // so banking can keep the center fixed on the ship's pivot.
                var centerInRoot = transform.InverseTransformPoint(worldCenter);
                _pivotToCenter = Quaternion.Inverse(_visualBaseRotation) * (centerInRoot - shipVisual.localPosition);
                _centerModel = true;
            }
            ApplyBank(0f);
        }

        public override void OnNetworkSpawn()
        {
            s_byClient[OwnerClientId] = this;

            PlayerName.OnValueChanged += OnPlayerNameChanged;

            if (swipeInputReader) swipeInputReader.enabled = false;

            if (IsServer)
            {
                string currentName = PlayerName.Value.ToString();
                if (string.IsNullOrWhiteSpace(currentName) || currentName == "Player")
                    PlayerName.Value = $"Player {OwnerClientId + 1}";
            }

            if (IsOwner)
            {
                _input = new FlightInput();
                Local = this;
                LocalSpawned?.Invoke(this);
            }
            else
            {
                transform.SetPositionAndRotation(_netPosition.Value, _netRotation.Value);
            }

            RefreshNameLabel();
        }

        public override void OnNetworkDespawn()
        {
            PlayerName.OnValueChanged -= OnPlayerNameChanged;

            if (s_byClient.TryGetValue(OwnerClientId, out var p) && p == this)
                s_byClient.Remove(OwnerClientId);

            if (Local == this) Local = null;
        }

        private void Update()
        {
            if (!IsSpawned) return;

            // Read input even during the finale cinematic (the winner taps to fire).
            if (IsOwner) _input.Update();
            if (PoseOverridden) return;

            var rm = RaceManager.Instance;
            var course = rm != null ? rm.Course : null;

            if (IsOwner) OwnerTick(rm, course);
            else RemoteTick();
        }

        // ---------- owner: flying ----------

        private void OwnerTick(RaceManager rm, RaceCourse course)
        {
            if (rm == null || course == null) return;

            var phase = rm.Phase.Value;
            if (phase != _lastPhase)
            {
                if (phase == RacePhase.Countdown) ResetForRace();
                _lastPhase = phase;
            }

            switch (phase)
            {
                case RacePhase.Lobby:
                case RacePhase.Countdown:
                case RacePhase.Results:
                    HoldOnGrid(course, rm.GetSlotIndex(OwnerClientId));
                    break;

                case RacePhase.Racing:
                case RacePhase.Finale:
                    Fly(rm, course, Time.deltaTime);
                    break;
            }

            PublishPose();
        }

        private void ResetForRace()
        {
            _speed = 0f;
            _boostMultiplier = 1f;
            _boostUntil = 0f;
            _autopilot = false;
            _progressHint = 0;
            _nextZone = 0;

            Progress.Value = 0f;
            FinishTime.Value = -1f;
            SignsCorrect.Value = 0;
            SignsAnswered.Value = 0;
            TopSpeed.Value = 0f;
            BoostsEarned.Value = 0;
        }

        private void HoldOnGrid(RaceCourse course, int slot)
        {
            var pose = course.GridSlot(slot < 0 ? 4 : slot);
            transform.SetPositionAndRotation(pose.position, pose.rotation);

            _yaw = pose.rotation.eulerAngles.y;
            _pitch = 0f;
            _roll = 0f;
            _speed = 0f;
            _progressHint = 0;
            ApplyBank(0f);
        }

        private void Fly(RaceManager rm, RaceCourse course, float dt)
        {
            bool cruising = _autopilot || HasFinished || rm.Phase.Value == RacePhase.Finale;
            var steer = cruising ? Vector2.zero : _input.Steer;

            // Speed: accelerate toward base speed x boost (slower while cruising).
            float target = baseSpeed * BoostMultiplier * (cruising ? autopilotSpeedFactor : 1f);
            _speed = Mathf.MoveTowards(_speed, target, acceleration * dt);

            // Heading the track wants here (aim at a point slightly ahead). Past the end of the
            // course, keep going straight along the final direction instead of turning back.
            float aheadDistance = Progress.Value + 25f;
            var lookAhead = course.PointAt(aheadDistance)
                + course.TangentAt(course.Length) * Mathf.Max(0f, aheadDistance - course.Length);
            var toTrack = (lookAhead - transform.position).normalized;
            float trackYaw = Mathf.Atan2(toTrack.x, toTrack.z) * Mathf.Rad2Deg;
            float trackPitch = -Mathf.Asin(Mathf.Clamp(toTrack.y, -1f, 1f)) * Mathf.Rad2Deg;

            if (cruising)
            {
                float k = 1f - Mathf.Exp(-4f * dt);
                _yaw = Mathf.LerpAngle(_yaw, trackYaw, k);
                _pitch = Mathf.LerpAngle(_pitch, trackPitch, k);
            }
            else
            {
                _yaw += steer.x * yawRate * dt;
                _pitch -= steer.y * pitchRate * dt; // drag up = nose up

                // Gentle assist toward the track on any axis you're not steering.
                float assist = 1f - Mathf.Exp(-steerAssist * dt);
                _yaw = Mathf.LerpAngle(_yaw, trackYaw, assist * (1f - Mathf.Abs(steer.x)));
                _pitch = Mathf.LerpAngle(_pitch, trackPitch, assist * (1f - Mathf.Abs(steer.y)));
            }
            _pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, _pitch), -maxPitch, maxPitch);

            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var position = transform.position + rotation * Vector3.forward * (_speed * dt);

            position = KeepInsideTube(course, position, ref rotation);
            transform.SetPositionAndRotation(position, rotation);

            // Bank into turns (visual only; the camera doesn't roll).
            _roll = Mathf.Lerp(_roll, -steer.x * bankAngle, 1f - Mathf.Exp(-6f * dt));
            ApplyBank(_roll);

            // Progress, zones, finish.
            float newProgress = Mathf.Min(course.GetProgress(position, ref _progressHint), course.Length);
            if (newProgress > Progress.Value) Progress.Value = newProgress;
            if (_speed > TopSpeed.Value) TopSpeed.Value = _speed;

            if (rm.Phase.Value != RacePhase.Racing || HasFinished) return;

            while (_nextZone < course.SignZoneDistances.Count && Progress.Value >= course.SignZoneDistances[_nextZone])
                SignZoneEntered?.Invoke(_nextZone++);

            if (Progress.Value >= course.Length - 2f)
            {
                FinishTime.Value = rm.RaceTime;
                _autopilot = false;
                ReportFinishRpc(FinishTime.Value);
            }
        }

        private Vector3 KeepInsideTube(RaceCourse course, Vector3 position, ref Quaternion rotation)
        {
            float progress = course.GetProgress(position, ref _progressHint);
            var center = course.PointAt(progress);
            var tangent = course.TangentAt(progress);
            var offset = position - center;
            float along = Vector3.Dot(offset, tangent);
            var lateral = offset - tangent * along;
            float limit = course.TubeRadius - 1.5f;

            if (lateral.magnitude <= limit)
            {
                _touchingWall = false;
                return position;
            }

            position = center + tangent * along + lateral.normalized * limit;

            if (!_touchingWall)
            {
                _touchingWall = true;
                _speed *= 0.85f;
                WallBumped?.Invoke();
            }

            // Turn the nose back toward the course.
            var inward = (center + tangent * 20f - position).normalized;
            float inYaw = Mathf.Atan2(inward.x, inward.z) * Mathf.Rad2Deg;
            float inPitch = -Mathf.Asin(Mathf.Clamp(inward.y, -1f, 1f)) * Mathf.Rad2Deg;
            _yaw = Mathf.LerpAngle(_yaw, inYaw, 0.15f);
            _pitch = Mathf.LerpAngle(_pitch, inPitch, 0.15f);
            rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            return position;
        }

        private void ApplyBank(float roll)
        {
            if (!shipVisual) return;
            var rotation = Quaternion.Euler(0f, 0f, roll) * _visualBaseRotation;
            shipVisual.localRotation = rotation;
            if (_centerModel) shipVisual.localPosition = -(rotation * _pivotToCenter);
        }

        private bool TryGetModelBounds(out Vector3 center)
        {
            center = Vector3.zero;
            var renderers = shipVisual.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Bounds b = default;
            foreach (var r in renderers)
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            if (any) center = b.center;
            return any;
        }

        private void PublishPose()
        {
            if (_netPosition.Value != transform.position) _netPosition.Value = transform.position;
            if (_netRotation.Value != transform.rotation) _netRotation.Value = transform.rotation;
            if (Mathf.Abs(_netSpeed.Value - _speed) > 0.1f) _netSpeed.Value = _speed;
            if (Mathf.Abs(_netRoll.Value - _roll) > 0.5f) _netRoll.Value = _roll;
        }

        // ---------- remote: follow the owner's pose ----------

        private void RemoteTick()
        {
            var target = _netPosition.Value;

            if ((transform.position - target).sqrMagnitude > 40f * 40f)
            {
                transform.SetPositionAndRotation(target, _netRotation.Value); // big jump (e.g. back to grid): snap
            }
            else
            {
                float k = 1f - Mathf.Exp(-remoteSmoothing * Time.deltaTime);
                transform.SetPositionAndRotation(
                    Vector3.Lerp(transform.position, target, k),
                    Quaternion.Slerp(transform.rotation, _netRotation.Value, k));
            }

            ApplyBank(_netRoll.Value);
        }

        // ---------- Sign Zone API (used by the quiz) ----------

        public void BeginAutopilot() => _autopilot = true;
        public void EndAutopilot() => _autopilot = false;

        /// <summary>Record a zone's answers and turn them into a speed boost (or a small slowdown).</summary>
        public void ApplySignZoneResult(int correct, int answered)
        {
            if (!IsOwner) return;

            SignsCorrect.Value += correct;
            SignsAnswered.Value += answered;

            if (correct > 0)
            {
                _boostMultiplier = 1f + boostPerCorrect * correct;
                _boostUntil = Time.time + boostSecondsBase + boostSecondsPerCorrect * correct;
                BoostsEarned.Value++;
                BoostStarted?.Invoke(_boostMultiplier);
            }
            else
            {
                _boostMultiplier = allWrongSlowdown;
                _boostUntil = Time.time + boostSecondsBase;
            }
        }

        // ---------- RPCs ----------

        [Rpc(SendTo.Server)]
        private void ReportFinishRpc(float raceTime)
        {
            if (RaceManager.Instance != null)
                RaceManager.Instance.ServerPlayerFinished(OwnerClientId, raceTime);
        }

        /// <summary>Raised on every other device when this ship's owner sends a camera frame (JPEG).</summary>
        public static event Action<ulong, byte[]> CamFrameReceived;

        /// <summary>Finale: the winner's device streams small JPEG snapshots of its camera to everyone else.</summary>
        [Rpc(SendTo.NotMe)]
        public void WinnerCamFrameRpc(byte[] jpg) => CamFrameReceived?.Invoke(OwnerClientId, jpg);

        /// <summary>Finale: the winner taps to fire. The server decides what gets hit.</summary>
        [Rpc(SendTo.Server)]
        public void FireLaserRpc()
        {
            if (RaceManager.Instance != null)
                RaceManager.Instance.ServerFireLaser(OwnerClientId);
        }

        [ServerRpc]
        public void SubmitNameServerRpc(string chosenName)
        {
            if (string.IsNullOrWhiteSpace(chosenName))
                chosenName = $"Player {OwnerClientId + 1}";

            chosenName = chosenName.Trim();

            if (chosenName.Length > 20)
                chosenName = chosenName.Substring(0, 20);

            PlayerName.Value = chosenName;
        }

        // ---------- name label ----------

        private void OnPlayerNameChanged(FixedString64Bytes oldValue, FixedString64Bytes newValue) => RefreshNameLabel();

        private void RefreshNameLabel()
        {
            if (nameText != null)
                nameText.text = PlayerName.Value.ToString();
        }

        public string GetPlayerName() => PlayerName.Value.ToString();
    }
}
