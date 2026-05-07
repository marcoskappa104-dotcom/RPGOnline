using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG.Managers
{
    /// <summary>
    /// GameManager v4
    ///
    /// CORREÇÕES v4:
    ///   - HashPassword documentado claramente como hash de transporte,
    ///     NÃO como armazenamento final. O servidor deve re-hashear com
    ///     bcrypt/Argon2 + salt por usuário antes de salvar no banco.
    ///   - Constantes de cena centralizadas.
    ///   - Logout limpa estado e volta para login.
    ///
    /// SEGURANÇA — IMPORTANTE PARA PRODUÇÃO:
    ///   O método atual usa SHA-256 com salt fixo apenas para ofuscar
    ///   a senha em trânsito. Para produção real:
    ///     1. Use TLS/WSS para criptografar a conexão (KCP + TLS).
    ///     2. No servidor, re-aplique bcrypt ou Argon2 com salt único por usuário.
    ///     3. Nunca armazene a senha em texto puro nem o hash SHA-256 direto.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        public string LoggedUsername { get; private set; } = "";

        public const string SCENE_LOGIN     = "LoginScene";
        public const string SCENE_CHARACTER = "CharacterScene";
        public const string SCENE_GAMEPLAY  = "GameplayScene";
        public const string GAME_VERSION    = "0.1.0-alpha";

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

        public void GoToCharacterSelect() => SceneManager.LoadScene(SCENE_CHARACTER);
        public void GoToGameplay()        => SceneManager.LoadScene(SCENE_GAMEPLAY);

        public void Logout()
        {
            LoggedUsername = "";
            SceneManager.LoadScene(SCENE_LOGIN);
        }

        /// <summary>
        /// Hash SHA-256 da senha para transporte seguro pela rede.
        ///
        /// ATENÇÃO — SEGURANÇA:
        ///   Este hash é usado APENAS para não enviar a senha em texto puro.
        ///   O servidor DEVE re-aplicar bcrypt/Argon2 com salt único por usuário
        ///   antes de armazenar no banco. Nunca confie apenas neste hash como
        ///   proteção final de senha.
        ///
        ///   Para produção: implemente TLS na camada de transporte e substitua
        ///   este método por um protocolo de challenge-response com nonce.
        ///
        ///   Troque APP_SALT antes de lançar o jogo e nunca o exponha
        ///   em repositórios públicos.
        /// </summary>
        public static string HashPassword(string password)
        {
#if UNITY_SERVER || UNITY_EDITOR
            // Salt apenas em builds de servidor/editor — cliente não precisa da mesma constante
            // se você implementar um fluxo correto de challenge-response
#endif
            const string APP_SALT = "RPG_ONLINE_SALT_2024_TROQUE_ANTES_DO_LAUNCH";

            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(password + APP_SALT);
            byte[] hash  = sha256.ComputeHash(bytes);
            return System.BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
}