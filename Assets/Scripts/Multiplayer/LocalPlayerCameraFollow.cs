using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// Race camera: chase cam behind the local ship, the shared shot during the finale,
    /// and speed feel: field-of-view kick, a high-speed rumble, speed-line streaks, and screen shake.
    /// </summary>
    public class LocalPlayerCameraFollow : MonoBehaviour
    {
        [Header("Chase")]
        [Tooltip("Camera position relative to the ship (behind and above). " +
                 "Renamed from 'offset' so the old side-view value saved in RaceScene (-6, 1.5, 0) no longer applies.")]
        public Vector3 chaseOffset = new Vector3(0f, 3.2f, -11f);
        [Tooltip("How far ahead of the ship the camera looks.")]
        public float lookAhead = 12f;
        public float positionLerp = 8f;
        public float rotationLerp = 8f;

        [Header("Field of view")]
        [SerializeField] private float baseFov = 62f;
        [Tooltip("Extra FOV at top boosted speed (sells the speed).")]
        [SerializeField] private float boostFov = 18f;

        [Header("Shake")]
        [SerializeField] private float shakeDecay = 3.5f;
        [SerializeField] private float maxShake = 1.2f;

        [Header("Speed feel")]
        [Tooltip("Constant camera rumble at full speed (meters). Grows with speed; strongest when boosting.")]
        [SerializeField] private float speedRumble = 0.07f;
        [Tooltip("Speed-line streaks per second at normal top speed (doubles while boosting).")]
        [SerializeField] private float speedLinesPerSecond = 60f;

        private static float s_shake;
        private ParticleSystem _speedLines;
        private ParticleSystem.EmissionModule _speedLinesEmission;

        private UnityEngine.Camera _cam;
        private Vector3 _smoothPos;
        private Quaternion _smoothRot = Quaternion.identity;
        private bool _hasPose;

        /// <summary>Add screen shake (0.1 = small bump, 1 = explosion).</summary>
        public static void AddShake(float amount) => s_shake = Mathf.Min(s_shake + amount, 1.5f);

        private void Awake()
        {
            _cam = GetComponent<UnityEngine.Camera>();
            BuildSpeedLines();
        }

        /// <summary>
        /// White streaks that spawn ahead of the camera and stay still in the world, so flying
        /// through them makes them rush past. Built in code: no prefab or material asset needed.
        /// </summary>
        private void BuildSpeedLines()
        {
            var go = new GameObject("SpeedLines (generated)");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, 45f);

            _speedLines = go.AddComponent<ParticleSystem>();
            _speedLines.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = _speedLines.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World; // stay put; the camera flies past
            main.startLifetime = 1.2f;
            main.startSpeed = 0f;
            main.startSize = 0.12f;
            main.startColor = new Color(1f, 1f, 1f, 0.55f);
            main.maxParticles = 400;
            main.playOnAwake = false;

            var shape = _speedLines.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(44f, 30f, 20f);

            _speedLinesEmission = _speedLines.emission;
            _speedLinesEmission.rateOverTime = 0f;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.cameraVelocityScale = 0.06f; // streak length grows with camera speed
            renderer.velocityScale = 0f;
            renderer.lengthScale = 1f;
            renderer.sharedMaterial = RaceVisuals.Unlit(Color.white);

            _speedLines.Play();
        }

        private void UpdateSpeedLines(float intensity)
        {
            if (!_speedLines) return;
            _speedLinesEmission.rateOverTime = speedLinesPerSecond * intensity;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            var rm = RaceManager.Instance;
            var local = NetworkRacePlayer.Local;

            Vector3 desiredPos;
            Vector3 lookTarget;
            float desiredFov = baseFov;

            float speedFeel = 0f; // 0 = stopped, 1 = normal top speed, 2 = big boost

            if (rm != null && rm.Finale != null && rm.Finale.Active)
            {
                desiredPos = rm.Finale.CameraPosition;
                lookTarget = rm.Finale.CameraLookAt;
                desiredFov = baseFov - 5f;
            }
            else if (local != null)
            {
                var target = local.transform; // the ship root (CameraFollowTarget on the prefab is offset to the side)
                // Follow the ship's heading, but not its banking roll.
                var heading = Quaternion.LookRotation(local.transform.forward, Vector3.up);
                desiredPos = target.position + heading * chaseOffset;
                lookTarget = target.position + local.transform.forward * lookAhead;

                float speed01 = local.BaseSpeed > 0f ? Mathf.Clamp01((local.Speed / local.BaseSpeed - 0.8f) / 1.0f) : 0f;
                desiredFov = baseFov + boostFov * speed01;

                bool racing = rm != null && rm.IsSpawned && rm.Phase.Value == RacePhase.Racing;
                if (racing && local.BaseSpeed > 0f)
                    speedFeel = Mathf.Clamp(local.Speed / local.BaseSpeed, 0f, 2f) * (local.BoostMultiplier > 1.01f ? 1.6f : 1f);
            }
            else
            {
                return; // nothing to look at yet (not connected)
            }

            if (!_hasPose)
            {
                _smoothPos = transform.position;
                _smoothRot = transform.rotation;
                _hasPose = true;
            }

            float kp = 1f - Mathf.Exp(-positionLerp * dt);
            float kr = 1f - Mathf.Exp(-rotationLerp * dt);

            _smoothPos = Vector3.Lerp(_smoothPos, desiredPos, kp);
            var desiredRot = Quaternion.LookRotation(lookTarget - _smoothPos, Vector3.up);
            _smoothRot = Quaternion.Slerp(_smoothRot, desiredRot, kr);

            if (_cam) _cam.fieldOfView = Mathf.Lerp(_cam.fieldOfView, desiredFov, 1f - Mathf.Exp(-4f * dt));

            UpdateSpeedLines(speedFeel);

            // Shake is layered on top of the smoothed pose, so it never drifts the camera.
            var pos = _smoothPos;
            var rot = _smoothRot;

            // High-speed rumble: smooth noise (not random jitter) so it reads as engine vibration.
            float rumble = speedRumble * speedFeel * speedFeel;
            if (rumble > 0.0001f)
            {
                float t = Time.time * 22f;
                pos += transform.right * ((Mathf.PerlinNoise(t, 0.1f) - 0.5f) * 2f * rumble)
                     + transform.up * ((Mathf.PerlinNoise(0.7f, t) - 0.5f) * 2f * rumble);
                rot *= Quaternion.Euler(0f, 0f, (Mathf.PerlinNoise(t, 3.3f) - 0.5f) * rumble * 12f);
            }

            if (s_shake > 0.001f)
            {
                float s = Mathf.Min(s_shake, maxShake);
                pos += Random.insideUnitSphere * (s * 0.6f);
                rot *= Quaternion.Euler(Random.Range(-1f, 1f) * s * 2f, Random.Range(-1f, 1f) * s * 2f, 0f);
                s_shake = Mathf.MoveTowards(s_shake, 0f, shakeDecay * dt);
            }
            transform.SetPositionAndRotation(pos, rot);
        }
    }
}
