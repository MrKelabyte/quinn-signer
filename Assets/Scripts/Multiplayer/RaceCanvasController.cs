using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Multiplayer
{
    /// <summary>
    /// Decides which screen is showing (connect / lobby / race HUD) from the network state
    /// and race phase, and handles disconnects: if the host leaves or the connection drops,
    /// everyone lands back on the connect screen with a message explaining why.
    /// </summary>
    public class RaceCanvasController : MonoBehaviour
    {
        public GameObject connectionCanvas;
        public GameObject lobbyCanvas;
        public GameObject raceHudCanvas;

        [Tooltip("Optional. Used to show disconnect messages. Found automatically if empty.")]
        [SerializeField] private RelayConnectionManager relay;

        /// <summary>True after this player closed the results screen (they see the lobby until the next race).</summary>
        private static bool s_resultsDismissed;

        private NetworkManager _subscribedTo;

        private const string TitleSceneName = "TitleScene";
        private static RaceCanvasController s_runner;
        private static bool s_exiting;

        /// <summary>Close the stats screen and go back to the lobby (local only).</summary>
        public static void DismissResults() => s_resultsDismissed = true;

        private void Start()
        {
            if (!relay) relay = FindFirstObjectByType<RelayConnectionManager>(FindObjectsInactive.Include); // one-time fallback
            s_runner = this;
            s_exiting = false;
            BuildExitButton();
            ShowConnection();
        }

        private void BuildExitButton()
        {
            if (!connectionCanvas || connectionCanvas.transform.Find("ExitButton (generated)")) return;
            var exit = UiFactory.Button("ExitButton (generated)", connectionCanvas.transform, "EXIT", new Color(0.8f, 0.2f, 0.25f, 1f), ExitToTitle, 40f);
            ((RectTransform)exit.transform).Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -30f), new Vector2(240f, 100f));
        }

        /// <summary>
        /// Leave the race entirely: disconnect, remove the NetworkManager (it survives scene loads,
        /// and a second copy would appear next time RaceScene opens), then load the title screen.
        /// The nav bar shows itself again there.
        /// </summary>
        public static void ExitToTitle()
        {
            if (s_exiting) return;
            s_exiting = true;
            if (s_runner) s_runner.StartCoroutine(ExitRoutine());
            else SceneManager.LoadScene(TitleSceneName);
        }

        private static IEnumerator ExitRoutine()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null)
            {
                if (nm.IsListening) nm.Shutdown();
                float timeout = Time.realtimeSinceStartup + 3f;
                while (nm != null && nm.ShutdownInProgress && Time.realtimeSinceStartup < timeout)
                    yield return null;
                if (nm != null) Destroy(nm.gameObject);
            }
            SceneManager.LoadScene(TitleSceneName);
        }

        private void OnDisable() => Unsubscribe();

        private void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm != _subscribedTo) Subscribe(nm);

            var rm = RaceManager.Instance;
            bool online = nm != null && nm.IsListening && rm != null && rm.IsSpawned;
            if (!online)
            {
                ShowConnection();
                return;
            }

            var phase = rm.Phase.Value;
            if (phase != RacePhase.Results) s_resultsDismissed = false;

            if (phase == RacePhase.Lobby || (phase == RacePhase.Results && s_resultsDismissed))
                ShowLobby();
            else
                ShowRaceHUD();
        }

        // ---------- disconnects ----------

        private void Subscribe(NetworkManager nm)
        {
            Unsubscribe();
            _subscribedTo = nm;
            if (!nm) return;
            nm.OnClientStopped += OnClientStopped;
            nm.OnTransportFailure += OnTransportFailure;
        }

        private void Unsubscribe()
        {
            if (!_subscribedTo) { _subscribedTo = null; return; }
            _subscribedTo.OnClientStopped -= OnClientStopped;
            _subscribedTo.OnTransportFailure -= OnTransportFailure;
            _subscribedTo = null;
        }

        private void OnClientStopped(bool wasHost)
        {
            string reason = _subscribedTo ? _subscribedTo.DisconnectReason : "";
            string message = wasHost
                ? "You closed the game."
                : string.IsNullOrEmpty(reason) ? "Disconnected: the host left the game." : $"Disconnected: {reason}";
            Report(message);
        }

        private void OnTransportFailure() => Report("Connection lost. Check Wi-Fi and try again.");

        private void Report(string message)
        {
            s_resultsDismissed = false;
            if (relay) relay.SetStatus(message);
            else Debug.Log(message);
            ShowConnection();
        }

        // ---------- screens ----------

        public void ShowConnection() => Show(connectionCanvas);
        public void ShowLobby() => Show(lobbyCanvas);
        public void ShowRaceHUD() => Show(raceHudCanvas);

        private void Show(GameObject target)
        {
            SetActive(connectionCanvas, target == connectionCanvas);
            SetActive(lobbyCanvas, target == lobbyCanvas);
            SetActive(raceHudCanvas, target == raceHudCanvas);
        }

        private static void SetActive(GameObject go, bool active)
        {
            if (go != null && go.activeSelf != active) go.SetActive(active);
        }
    }
}
