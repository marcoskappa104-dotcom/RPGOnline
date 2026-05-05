using UnityEngine;
using Mirror;

namespace RPG.Network
{
    /// <summary>
    /// NetworkConnectionBootstrapper v2
    ///
    /// CORREÇÕES:
    ///   - Removido redirect para ServerScene (causava erro porque a cena
    ///     pode não estar no Build Settings ainda).
    ///     O ServerEntryPoint faz esse redirect ANTES, no Awake.
    ///   - Não tenta reconectar ao ser destruído (OnDestroy simplificado).
    ///   - Servidor: apenas StartServer(). Ponto.
    ///   - Cliente: apenas StartClient(). Ponto.
    /// </summary>
    public class NetworkConnectionBootstrapper : MonoBehaviour
    {
        [Header("Conexão (apenas cliente)")]
        [SerializeField] public string serverAddress = "localhost";
        [SerializeField] public ushort serverPort    = 7777;

        private void Start()
        {
            // Já está rodando? Não faz nada.
            if (NetworkServer.active || NetworkClient.active)
            {
                Debug.Log("[Bootstrapper] Rede já ativa — ignorando Start().");
                return;
            }

            bool isServer = IsServerBuild();
            bool isHost   = IsHostBuild();

            // Configura porta KCP
            var kcp = GetComponentInChildren<kcp2k.KcpTransport>()
                   ?? FindObjectOfType<kcp2k.KcpTransport>();
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
                NetworkManager.singleton.networkAddress = serverAddress;
                NetworkManager.singleton.StartHost();
            }
            else
            {
                Debug.Log($"[Bootstrapper] CLIENTE | {serverAddress}:{serverPort}");
                NetworkManager.singleton.networkAddress = serverAddress;
                NetworkManager.singleton.StartClient();
            }
        }

        public static bool IsServerBuild()
        {
            if (Application.isBatchMode) return true;
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.ToLower() == "-server") return true;
            return false;
        }

        public static bool IsHostBuild()
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
                if (arg.ToLower() == "-host") return true;
            return false;
        }
    }
}