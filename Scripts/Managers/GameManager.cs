using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG.Managers
{
    /// <summary>
    /// GameManager v3 — Atualizado para usar DatabaseManager.
    ///
    /// MUDANÇAS em relação à v2:
    ///   - Sem referência a SaveManager (removido do projeto).
    ///   - HashPassword mantido aqui (usado pelo ClientAuthHandler para
    ///     fazer hash antes de enviar pela rede).
    ///   - Demais funcionalidades inalteradas.
    ///
    /// REMOVER DO PROJETO:
    ///   DELETE: SaveManager.cs
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        public string LoggedUsername { get; private set; } = "";

        public const string SCENE_LOGIN      = "LoginScene";
        public const string SCENE_CHARACTER  = "CharacterScene";
        public const string SCENE_GAMEPLAY   = "GameplayScene";
        public const string GAME_VERSION     = "0.1.0-alpha";

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log($"[GameManager] Iniciado — versão {GAME_VERSION}");
        }

        public void SetLoggedUsername(string username)
        {
            LoggedUsername = username;
            Debug.Log($"[GameManager] Usuário logado: {username}");
        }

        public void GoToCharacterSelect()  => SceneManager.LoadScene(SCENE_CHARACTER);
        public void GoToGameplay()         => SceneManager.LoadScene(SCENE_GAMEPLAY);

        public void Logout()
        {
            LoggedUsername = "";
            SceneManager.LoadScene(SCENE_LOGIN);
        }

        /// <summary>
        /// Hash SHA-256 para envio seguro de senha ao servidor.
        /// O servidor armazena e compara apenas o hash — nunca a senha em texto.
        /// Em produção com banco real externo: adicione salt no servidor.
        /// </summary>
        public static string HashPassword(string password)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(password);
            byte[] hash  = sha256.ComputeHash(bytes);
            return System.BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
}
