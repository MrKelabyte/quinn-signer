using UnityEngine;
using TMPro;
using Unity.Netcode;

namespace Multiplayer
{
    /// <summary>
    /// Simple status text + name entry. Not used in RaceScene right now (RaceHUD and
    /// LobbyUIController cover this), but kept working in case a scene references it.
    /// </summary>
    public class RaceUIController : MonoBehaviour
    {
        public TMP_InputField nameInput;
        public TMP_Text statusText;

        private void Update()
        {
            var rm = RaceManager.Instance;
            if (rm == null || !rm.IsSpawned || statusText == null)
                return;

            switch (rm.Phase.Value)
            {
                case RacePhase.Lobby:
                    statusText.text = rm.GetPlayerCount() < rm.MinPlayers ? "Waiting for players..." : "Get Ready!";
                    break;
                case RacePhase.Countdown:
                case RacePhase.Racing:
                    statusText.text = "Race in progress!";
                    break;
                default:
                    var local = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : RaceManager.NoWinner;
                    statusText.text = rm.IsWinner(local) ? "You Win!" : "You Lose!";
                    break;
            }
        }

        public void ApplyLocalName()
        {
            string chosen = nameInput != null ? nameInput.text : "Player";
            if (NetworkRacePlayer.Local != null)
                NetworkRacePlayer.Local.SubmitNameServerRpc(chosen);
        }
    }
}
