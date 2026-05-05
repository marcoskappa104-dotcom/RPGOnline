using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG.Network
{
    /// <summary>
    /// ServerEntryPoint v2
    ///
    /// CORREÇÃO DO LOG:
    ///   "Scene with build index: 3 couldn't be loaded because it has not been
    ///    added to the build settings."
    ///
    /// SOLUÇÃO:
    ///   Na build do Dedicated Server, NÃO precisa de uma ServerScene separada.
    ///   O Unity Dedicated Server já compila sem gráficos — todos os shaders
    ///   são ignorados automaticamente (você já viu isso no log: "Forcing GfxDevice: Null").
    ///
    ///   Portanto, o servidor pode usar a MESMA GameplayScene do cliente.
    ///   Ele simplesmente ignora UI, câmera e shaders — isso é normal e correto.
    ///
    ///   Este script usa o NOME da cena em vez do index para não depender
    ///   da ordem no Build Settings.
    ///
    /// COLOQUE na LoginScene.
    /// </summary>
    public class ServerEntryPoint : MonoBehaviour
    {
        [Header("Nome da cena do servidor (deve estar no Build Settings)")]
        [Tooltip("Use o nome exato da cena, sem extensão. Ex: GameplayScene")]
        [SerializeField] private string serverSceneName = "GameplayScene";

        private void Awake()
        {
            if (!NetworkConnectionBootstrapper.IsServerBuild()) return;

            string currentScene = SceneManager.GetActiveScene().name;
            if (currentScene == serverSceneName) return;

            Debug.Log($"[ServerEntryPoint] Servidor detectado. Carregando '{serverSceneName}'...");
            SceneManager.LoadScene(serverSceneName);
        }
    }
}