using UnityEngine;
using Mirror;
using RPG.Managers;

namespace RPG.Network
{
    /// <summary>
    /// NetworkGameplayBootstrapper — detecta automaticamente o modo de execução:
    ///
    ///   -batchmode (Server Build Unity) → StartServer()  (headless, sem gráficos)
    ///   -server    (argumento manual)   → StartServer()
    ///   -host      (argumento manual)   → StartHost()    (servidor + cliente local)
    ///   Normal                          → StartClient()  (jogador comum)
    ///
    /// CORREÇÃO: verifica SelectedCharacter ANTES de tentar conectar como cliente,
    /// evitando crash quando a cena é carregada diretamente no Editor sem login.
    /// </summary>
    public class NetworkGameplayBootstrapper : MonoBehaviour
    {
        [Header("Conexão")]
        [Tooltip("localhost para testes locais. IP/domínio para produção.")]
        [SerializeField] public string serverAddress = "localhost";
        [SerializeField] public ushort serverPort    = 7777;

        private void Start()
        {
            bool isServer = IsServerBuild();
            bool isHost   = IsHostBuild();

            // Clientes precisam de personagem selecionado
            if (!isServer && GameManager.Instance?.SelectedCharacter == null)
            {
                Debug.LogWarning("[NetworkBootstrapper] Nenhum personagem selecionado — voltando ao login.");
                UnityEngine.SceneManagement.SceneManager.LoadScene(GameManager.SCENE_LOGIN);
                return;
            }

            // Configura porta no transporte KCP
            var kcp = FindObjectOfType<kcp2k.KcpTransport>();
            if (kcp != null)
                kcp.Port = serverPort;
            else
                Debug.LogWarning("[NetworkBootstrapper] KcpTransport não encontrado na cena!");

            if (isServer)
            {
                Debug.Log($"[NetworkBootstrapper] Modo: SERVIDOR DEDICADO | Porta: {serverPort}");
                NetworkManager.singleton.StartServer();
            }
            else if (isHost)
            {
                Debug.Log($"[NetworkBootstrapper] Modo: HOST (servidor+cliente) | Porta: {serverPort}");
                NetworkManager.singleton.StartHost();
            }
            else
            {
                Debug.Log($"[NetworkBootstrapper] Modo: CLIENTE | Conectando em {serverAddress}:{serverPort}");
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
