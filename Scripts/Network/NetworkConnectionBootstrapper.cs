using UnityEngine;
using Mirror;
using RPG.Managers;

namespace RPG.Network
{
    /// <summary>
    /// NetworkConnectionBootstrapper — ponto de entrada de TODAS as cenas com rede.
    ///
    /// COLOQUE NA CENA DE LOGIN.
    ///
    /// - Servidor dedicado (-batchmode ou -server): inicia o servidor.
    /// - Cliente: conecta ao servidor. NÃO spawna player automaticamente.
    ///   O spawn ocorre após login + seleção de personagem.
    /// </summary>
    public class NetworkConnectionBootstrapper : MonoBehaviour
    {
        [Header("Conexão")]
        [SerializeField] public string serverAddress = "localhost";
        [SerializeField] public ushort serverPort    = 7777;

        private void Start()
        {
            bool isServer = IsServerBuild();
            bool isHost   = IsHostBuild();

            // Configura KCP
            var kcp = FindObjectOfType<kcp2k.KcpTransport>();
            if (kcp != null)
                kcp.Port = serverPort;
            else
                Debug.LogWarning("[Bootstrapper] KcpTransport não encontrado!");

            if (isServer)
            {
                Debug.Log($"[Bootstrapper] SERVIDOR DEDICADO | Porta:{serverPort}");
                NetworkManager.singleton.StartServer();
            }
            else if (isHost)
            {
                Debug.Log($"[Bootstrapper] HOST | Porta:{serverPort}");
                NetworkManager.singleton.StartHost();
            }
            else
            {
                Debug.Log($"[Bootstrapper] CLIENTE | {serverAddress}:{serverPort}");
                NetworkManager.singleton.networkAddress = serverAddress;
                NetworkManager.singleton.StartClient();
            }
        }

        private bool IsServerBuild()
        {
            if (Application.isBatchMode) return true;
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.ToLower() == "-server") return true;
            return false;
        }

        private bool IsHostBuild()
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.ToLower() == "-host") return true;
            return false;
        }

        private void OnDestroy()
        {
            if (NetworkServer.active && NetworkClient.isConnected)
                NetworkManager.singleton?.StopHost();
            else if (NetworkClient.isConnected)
                NetworkManager.singleton?.StopClient();
            else if (NetworkServer.active)
                NetworkManager.singleton?.StopServer();
        }
    }
}
