using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;

namespace Multiplayer
{
    public class LobbyUIController : MonoBehaviour
    {
        [Header("UI")]
        public TMP_Text lobbyStatusText;
        public TMP_Text playerCountText;
        public TMP_Text joinCodeText;
        public TMP_Text playerListText;
        public Button startRaceButton;

        [Header("Optional")]
        public TMP_InputField playerNameInput;
        [Tooltip("Found automatically if empty.")]
        [SerializeField] private RelayConnectionManager relayManager;

        private void Start()
        {
            if (!relayManager) relayManager = FindFirstObjectByType<RelayConnectionManager>(FindObjectsInactive.Include); // one-time fallback
            BuildLeaveButton();
        }

        private void BuildLeaveButton()
        {
            var leave = UiFactory.Button("LeaveButton (generated)", transform, "LEAVE", new Color(0.8f, 0.2f, 0.25f, 1f), Leave, 40f);
            ((RectTransform)leave.transform).Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -30f), new Vector2(240f, 100f));
        }

        private void Update()
        {
            var rm = RaceManager.Instance;
            var nm = NetworkManager.Singleton;
            if (rm == null || nm == null || !rm.IsSpawned)
                return;

            bool isHost = nm.IsHost;
            int playerCount = rm.GetPlayerCount();
            bool canStart = playerCount >= rm.MinPlayers;

            if (startRaceButton != null)
            {
                startRaceButton.gameObject.SetActive(isHost);
                startRaceButton.interactable = isHost && canStart;
            }

            if (playerCountText != null)
                playerCountText.text = $"Players: {playerCount}/{rm.maxPlayers}";

            if (joinCodeText != null)
            {
                var code = relayManager != null ? relayManager.CurrentJoinCode : "";
                joinCodeText.text = string.IsNullOrWhiteSpace(code) ? "Join Code: ----" : $"Join Code: {code}";
            }

            if (playerListText != null)
            {
                var sb = new StringBuilder();
                var players = rm.GetPlayersInOrder();
                for (int i = 0; i < players.Count; i++)
                {
                    var you = players[i] == NetworkRacePlayer.Local ? "  (you)" : "";
                    sb.AppendLine($"{i + 1}. {players[i].GetPlayerName()}{you}");
                }
                playerListText.text = sb.ToString();
            }

            if (lobbyStatusText != null)
            {
                if (!canStart)
                    lobbyStatusText.text = "Waiting for more players...";
                else if (isHost)
                    lobbyStatusText.text = $"Ready! Press Start ({rm.MinPlayers}-{rm.maxPlayers} players).";
                else
                    lobbyStatusText.text = "Waiting for the host to start.";
            }
        }

        public void OnStartRacePressed()
        {
            if (RaceManager.Instance == null) return;
            RaceManager.Instance.RequestStartRaceRpc();
        }

        public void OnApplyNamePressed()
        {
            string chosenName = playerNameInput != null ? playerNameInput.text : "Player";
            if (NetworkRacePlayer.Local != null)
                NetworkRacePlayer.Local.SubmitNameServerRpc(chosenName);
        }

        /// <summary>Leave the game and go back to the title screen. If the host leaves, everyone else
        /// is sent back to the connect screen.</summary>
        public void Leave() => RaceCanvasController.ExitToTitle();
    }
}
