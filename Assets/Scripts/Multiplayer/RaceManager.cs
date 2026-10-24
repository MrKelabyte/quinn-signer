using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Multiplayer
{
    /// <summary>
    /// Owns the race rules. The server decides the phase, the course seed, the finish order,
    /// the winner and what the finale lasers hit. Clients only react to that synced state.
    ///
    /// Timing uses the network's shared server clock (RaceStartTime + ServerTime) instead of
    /// coroutines, so every device shows the same countdown and race time.
    /// </summary>
    public class RaceManager : NetworkBehaviour
    {
        public const ulong NoWinner = ulong.MaxValue;

        public static RaceManager Instance { get; private set; }

        [Header("Race")]
        public int countdownSeconds = 3;
        public int minPlayersToStart = 2;
        public int maxPlayers = 4;
        [Tooltip("Lets the host start a race alone (Editor and builds), e.g. to try the course solo.")]
        [SerializeField] private bool allowSoloStart = true;
        [Tooltip("How long the 'WINNER!' moment lasts before the finale starts.")]
        [SerializeField] private float winnerBannerSeconds = 2f;

        [Header("Finale")]
        [Tooltip("Seconds before the winner can start firing (camera move-in).")]
        [SerializeField] private float finaleIntroSeconds = 2f;
        [Tooltip("Laser hits needed to blow up each other ship.")]
        [SerializeField] private int hitsToDestroy = 12;
        [Tooltip("If the winner stops tapping, remaining ships blow up after this many seconds.")]
        [SerializeField] private float finaleMaxSeconds = 15f;
        [Tooltip("Pause after the last explosion before the stats screen.")]
        [SerializeField] private float finaleOutroSeconds = 2.5f;

        [Header("Course")]
        [SerializeField] private CourseSettings courseSettings = new CourseSettings();

        public NetworkVariable<RacePhase> Phase = new(RacePhase.Lobby, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public NetworkVariable<int> CourseSeed = new(1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public NetworkVariable<double> RaceStartTime = new(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public NetworkVariable<double> FinaleStartTime = new(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        public NetworkVariable<ulong> WinnerClientId = new(NoWinner, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Racers in join order (also their grid slots).</summary>
        public NetworkList<ulong> Players;

        /// <summary>Client ids in the order they crossed the finish line.</summary>
        public NetworkList<ulong> FinishOrder;

        /// <summary>Raised on every device when a finale laser is fired.</summary>
        public event Action<ulong, ulong> LaserFired;          // shooter, target

        /// <summary>Raised on every device when a ship is blown up in the finale.</summary>
        public event Action<ulong> ShipDestroyed;              // target

        /// <summary>Raised on every device when the course is (re)built.</summary>
        public event Action<RaceCourse> CourseBuilt;

        public RaceCourse Course { get; private set; }
        public RaceFinale Finale { get; private set; }

        public int MinPlayers => allowSoloStart ? 1 : Mathf.Max(1, minPlayersToStart);
        public float FinaleIntroSeconds => finaleIntroSeconds;
        public int HitsToDestroy => hitsToDestroy;

        /// <summary>Shared network clock (estimated server time on clients).</summary>
        public double Now => IsSpawned ? NetworkManager.ServerTime.Time : 0d;

        /// <summary>Seconds since GO (0 before the race starts).</summary>
        public float RaceTime => Phase.Value >= RacePhase.Racing ? (float)Math.Max(0d, Now - RaceStartTime.Value) : 0f;

        /// <summary>Seconds until GO during the countdown.</summary>
        public float CountdownRemaining => (float)Math.Max(0d, RaceStartTime.Value - Now);

        public bool IsWinner(ulong clientId) => WinnerClientId.Value == clientId && WinnerClientId.Value != NoWinner;

        // server-only state
        private double _firstFinishAt = -1d;
        private double _finaleDeadline, _finaleEndsAt = -1d;
        private readonly Dictionary<ulong, int> _finaleHp = new Dictionary<ulong, int>();

        // ---------- lifecycle ----------

        private void Awake()
        {
            Instance = this;
            Players = new NetworkList<ulong>();
            FinishOrder = new NetworkList<ulong>();
            Finale = new RaceFinale(this);
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Course?.Destroy();
            Finale?.Dispose();
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            CourseSeed.OnValueChanged += OnSeedChanged;

            if (IsServer)
            {
                NetworkManager.OnClientConnectedCallback += OnClientConnected;
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;

                Players.Clear();
                FinishOrder.Clear();
                foreach (var id in NetworkManager.ConnectedClientsIds)
                    AddPlayer(id);

                WinnerClientId.Value = NoWinner;
                CourseSeed.Value = NewSeed();
                Phase.Value = RacePhase.Lobby;
            }

            BuildCourse(CourseSeed.Value);
        }

        public override void OnNetworkDespawn()
        {
            CourseSeed.OnValueChanged -= OnSeedChanged;

            if (IsServer && NetworkManager != null)
            {
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            Finale.Reset();
            Course?.Destroy();
            Course = null;
        }

        private void Update()
        {
            if (!IsSpawned) return;

            if (IsServer) ServerTick();
            Finale.Tick(Time.deltaTime);
        }

        // ---------- course ----------

        private void OnSeedChanged(int oldSeed, int newSeed) => BuildCourse(newSeed);

        private void BuildCourse(int seed)
        {
            if (Course != null && Course.Seed == seed) return;
            Course?.Destroy();
            Course = new RaceCourse(courseSettings, seed);
            CourseBuilt?.Invoke(Course);
        }

        private static int NewSeed() => UnityEngine.Random.Range(1, int.MaxValue);

        // ---------- players ----------

        public int GetSlotIndex(ulong clientId) => Players.IndexOf(clientId);

        public int GetPlayerCount() => Players.Count;

        /// <summary>Racers in join order (skips any whose ship hasn't spawned yet).</summary>
        public List<NetworkRacePlayer> GetPlayersInOrder()
        {
            var list = new List<NetworkRacePlayer>(Players.Count);
            foreach (var id in Players)
                if (NetworkRacePlayer.All.TryGetValue(id, out var p) && p) list.Add(p);
            return list;
        }

        /// <summary>Racers sorted by current standing (finished first by finish order, then by progress).</summary>
        public List<NetworkRacePlayer> GetStandings()
        {
            var list = GetPlayersInOrder();
            list.Sort((a, b) =>
            {
                int fa = FinishOrder.IndexOf(a.OwnerClientId), fb = FinishOrder.IndexOf(b.OwnerClientId);
                if (fa >= 0 || fb >= 0)
                {
                    if (fa < 0) return 1;
                    if (fb < 0) return -1;
                    return fa.CompareTo(fb);
                }
                return b.Progress.Value.CompareTo(a.Progress.Value);
            });
            return list;
        }

        private void OnClientConnected(ulong clientId) => AddPlayer(clientId);

        private void AddPlayer(ulong clientId)
        {
            if (Players.Contains(clientId)) return;
            if (Players.Count >= maxPlayers)
            {
                Debug.LogWarning($"RaceManager: client {clientId} joined but the race is full ({maxPlayers}). They will spectate.");
                return;
            }
            Players.Add(clientId);
        }

        private void OnClientDisconnected(ulong clientId)
        {
            Players.Remove(clientId);
            FinishOrder.Remove(clientId);
            _finaleHp.Remove(clientId);

            if (Phase.Value != RacePhase.Lobby && Players.Count == 0)
                Phase.Value = RacePhase.Lobby;
        }

        // ---------- starting ----------

        /// <summary>Called by the host's Start button (from the lobby or the results screen).</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestStartRaceRpc(RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId) return; // host only
            if (Phase.Value != RacePhase.Lobby && Phase.Value != RacePhase.Results) return;
            if (Players.Count < MinPlayers) return;

            FinishOrder.Clear();
            WinnerClientId.Value = NoWinner;
            _firstFinishAt = -1d;
            _finaleEndsAt = -1d;
            _finaleHp.Clear();

            foreach (var p in GetPlayersInOrder())
                p.LasersFired.Value = 0;

            CourseSeed.Value = NewSeed();
            RaceStartTime.Value = Now + countdownSeconds;
            Phase.Value = RacePhase.Countdown;
        }

        // ---------- server loop ----------

        private void ServerTick()
        {
            switch (Phase.Value)
            {
                case RacePhase.Countdown:
                    if (Now >= RaceStartTime.Value) Phase.Value = RacePhase.Racing;
                    break;

                case RacePhase.Racing:
                    bool everyoneDone = FinishOrder.Count > 0 && FinishOrder.Count >= Players.Count;
                    if (_firstFinishAt >= 0d && (Now >= _firstFinishAt + winnerBannerSeconds || everyoneDone))
                        EnterFinale();
                    break;

                case RacePhase.Finale:
                    if (_finaleEndsAt < 0d && Now >= _finaleDeadline)
                        DestroyAllRemaining();
                    if (_finaleEndsAt >= 0d && Now >= _finaleEndsAt)
                        Phase.Value = RacePhase.Results;
                    break;
            }
        }

        /// <summary>Server: a player crossed the finish line.</summary>
        public void ServerPlayerFinished(ulong clientId, float raceTime)
        {
            if (!IsServer || Phase.Value != RacePhase.Racing) return;
            if (FinishOrder.Contains(clientId)) return;

            FinishOrder.Add(clientId);

            if (WinnerClientId.Value == NoWinner)
            {
                WinnerClientId.Value = clientId;
                _firstFinishAt = Now;
            }
        }

        // ---------- finale ----------

        private void EnterFinale()
        {
            _finaleHp.Clear();
            foreach (var id in Players)
                if (id != WinnerClientId.Value) _finaleHp[id] = hitsToDestroy;

            FinaleStartTime.Value = Now;
            _finaleDeadline = Now + finaleIntroSeconds + finaleMaxSeconds;
            _finaleEndsAt = _finaleHp.Count == 0 ? Now + finaleIntroSeconds + 3d : -1d; // solo: short victory lap
            Phase.Value = RacePhase.Finale;
        }

        /// <summary>Server: the winner tapped to fire.</summary>
        public void ServerFireLaser(ulong shooterId)
        {
            if (!IsServer || Phase.Value != RacePhase.Finale) return;
            if (shooterId != WinnerClientId.Value) return;
            if (Now < FinaleStartTime.Value + finaleIntroSeconds) return;

            // Target the first ship (in join order) that is still alive.
            ulong target = NoWinner;
            foreach (var id in Players)
            {
                if (_finaleHp.TryGetValue(id, out var hp) && hp > 0) { target = id; break; }
            }
            if (target == NoWinner) return;

            _finaleHp[target]--;
            if (NetworkRacePlayer.All.TryGetValue(shooterId, out var shooter)) shooter.LasersFired.Value++;

            LaserFiredRpc(shooterId, target);

            if (_finaleHp[target] <= 0)
            {
                ShipDestroyedRpc(target);
                if (!AnyAlive()) _finaleEndsAt = Now + finaleOutroSeconds;
            }
        }

        private void DestroyAllRemaining()
        {
            var ids = new List<ulong>(_finaleHp.Keys);
            foreach (var id in ids)
            {
                if (_finaleHp[id] <= 0) continue;
                _finaleHp[id] = 0;
                ShipDestroyedRpc(id);
            }
            _finaleEndsAt = Now + finaleOutroSeconds;
        }

        private bool AnyAlive()
        {
            foreach (var hp in _finaleHp.Values)
                if (hp > 0) return true;
            return false;
        }

        [Rpc(SendTo.ClientsAndHost)]
        private void LaserFiredRpc(ulong shooter, ulong target) => LaserFired?.Invoke(shooter, target);

        [Rpc(SendTo.ClientsAndHost)]
        private void ShipDestroyedRpc(ulong target) => ShipDestroyed?.Invoke(target);
    }
}
