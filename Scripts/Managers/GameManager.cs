using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG.Managers
{
    /// <summary>
    /// GameManager v2 — Server-Authoritative
    ///
    /// REMOVIDO:
    ///   - CurrentAccount  (dados de conta ficam no servidor)
    ///   - SelectedCharacter (dados do personagem ficam no servidor)
    ///
    /// MANTIDO:
    ///   - LoggedUsername: referência mínima para UI exibir o nome do usuário logado.
    ///   - Constantes de cena e HashPassword (usado pelo ClientAuthHandler).
    ///   - GoToCharacterSelect / GoToGameplay agora apenas carregam cenas.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        // ── Sessão (mínima — dados reais ficam no servidor) ────────────
        public string LoggedUsername { get; private set; } = "";

        // ── Constantes de cenas ────────────────────────────────────────
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

        // ── Sessão ─────────────────────────────────────────────────────

        public void SetLoggedUsername(string username)
        {
            LoggedUsername = username;
            Debug.Log($"[GameManager] Usuário logado: {username}");
        }

        // ── Navegação ──────────────────────────────────────────────────

        /// <summary>
        /// Vai para a tela de seleção/criação de personagem.
        /// Só deve ser chamado após login confirmado pelo servidor.
        /// </summary>
        public void GoToCharacterSelect()
        {
            SceneManager.LoadScene(SCENE_CHARACTER);
        }

        /// <summary>
        /// Vai para o jogo. Chamado após seleção de personagem confirmada.
        /// O player é spawnado pelo servidor — cliente apenas carrega a cena.
        /// </summary>
        public void GoToGameplay()
        {
            SceneManager.LoadScene(SCENE_GAMEPLAY);
        }

        public void Logout()
        {
            LoggedUsername = "";
            SceneManager.LoadScene(SCENE_LOGIN);
        }

        // ── Utilitários ────────────────────────────────────────────────

        /// <summary>
        /// Hash SHA-256 para envio seguro de senha ao servidor.
        /// Em produção com banco real: use bcrypt/Argon2 com salt no servidor.
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
