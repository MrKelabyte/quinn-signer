using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

namespace Multiplayer
{
    public class RelayConnectionManager : MonoBehaviour
    {
        [Header("References")]
        public NetworkManager networkManager;
        public UnityTransport unityTransport;

        [Header("UI")]
        public TMP_InputField joinCodeInput;
        public TMP_Text joinCodeText;
        public TMP_Text statusText;

        [Header("Relay")]
        public int maxConnections = 4;
        public string connectionType = "dtls";

        public string CurrentJoinCode { get; private set; } = "";

        private async void Start()
        {
            if (networkManager == null)
                networkManager = FindFirstObjectByType<NetworkManager>(); // one-time fallback

            if (unityTransport == null && networkManager != null)
                unityTransport = networkManager.GetComponent<UnityTransport>();

            if (networkManager == null)
            {
                SetStatus("No NetworkManager found in scene.");
                Debug.LogError("RelayConnectionManager: No NetworkManager found.");
                return;
            }

            if (unityTransport == null)
            {
                SetStatus("No UnityTransport found on NetworkManager.");
                Debug.LogError("RelayConnectionManager: No UnityTransport found.");
                return;
            }

            await InitializeServicesIfNeeded();
        }

        private async Task InitializeServicesIfNeeded()
        {
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized)
                    await UnityServices.InitializeAsync();

                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();

                SetStatus("Unity Services initialized.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetStatus("Failed to initialize Unity Services.");
            }
        }

        public async void CreateRelayHost()
        {
            if (networkManager == null || unityTransport == null)
            {
                SetStatus("Missing NetworkManager or UnityTransport.");
                return;
            }

            await InitializeServicesIfNeeded();

            try
            {
                SetStatus("Creating Relay allocation...");

                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
                unityTransport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, connectionType));

                string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
                CurrentJoinCode = joinCode;

                if (joinCodeText != null)
                    joinCodeText.text = joinCode;

                bool started = networkManager.StartHost();
                SetStatus(started ? $"Host started. Code: {joinCode}" : "Host failed to start.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetStatus("Failed to create Relay host.");
            }
        }

        public async void JoinRelayClient()
        {
            if (networkManager == null || unityTransport == null)
            {
                SetStatus("Missing NetworkManager or UnityTransport.");
                return;
            }

            await InitializeServicesIfNeeded();

            string joinCode = joinCodeInput != null ? joinCodeInput.text.Trim() : "";

            if (string.IsNullOrEmpty(joinCode))
            {
                SetStatus("Enter a join code first.");
                return;
            }

            try
            {
                SetStatus("Joining Relay allocation...");

                JoinAllocation allocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
                unityTransport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, connectionType));

                CurrentJoinCode = joinCode;

                bool started = networkManager.StartClient();
                SetStatus(started ? "Client joined successfully." : "Client failed to start.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetStatus("Failed to join Relay.");
            }
        }

        public void SetStatus(string message)
        {
            Debug.Log(message);

            if (statusText != null)
                statusText.text = message;
        }
    }
}