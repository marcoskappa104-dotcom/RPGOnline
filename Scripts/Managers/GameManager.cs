using UnityEngine;
using UnityEngine.SceneManagement;

namespace RPG.Managers
{
    /// <summary>
    /// GameManager v5
    ///
    /// CORREÇÕES v5:
    ///   - HashPassword: o APP_SALT foi movido para um método separado e
    ///     SOMENTE compilado no servidor (UNITY_SERVER | UNITY_EDITOR).
    ///     O cliente usa um hash simples SEM salt — o salt real vive apenas
    ///     no binário do servidor, não no cliente distribuído.
    ///
    ///     FLUXO CORRETO:
    ///       Cliente  → envia SHA-256(senha) sem salt
    ///       Servidor → recebe hash, aplica salt+bcrypt antes de comparar/salvar
    ///
    ///     PARA PRODUÇÃO REAL: implemente TLS (KCP+TLS ou WebSocket+WSS) e
    ///     troque para challenge-response com nonce por sessão.
    ///
    ///   - Constantes de cena centralizadas (sem alteração).
    ///   - Logout limpa estado e volta para login (sem alteração).
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
        /// Hash SHA-256 da senha para transporte.
        ///
        /// CLIENTE: hash sem salt (o salt nunca deve estar no binário do cliente).
        /// SERVIDOR: ao receber, aplica bcrypt/Argon2 com salt único por usuário.
        ///
        /// IMPORTANTE: este método é chamado TANTO pelo cliente quanto pelo servidor.
        /// O servidor usa HashPassword() apenas para receber o valor do cliente e
        /// então re-hasheia com ServerHashForStorage() antes de salvar no banco.
        ///
        /// TROQUE PARA PRODUÇÃO: implemente TLS e challenge-response com nonce.
        /// </summary>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return "";

            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(password);
            byte[] hash  = sha256.ComputeHash(bytes);
            return System.BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

#if UNITY_SERVER || UNITY_EDITOR
        /// <summary>
        /// Hash para armazenamento no servidor — NUNCA chame do cliente.
        /// Aplica salt server-side antes de salvar no banco.
        ///
        /// ATENÇÃO: Para produção real substitua por bcrypt ou Argon2.
        /// Este salt deve estar em variável de ambiente, NÃO no código-fonte.
        /// </summary>
        public static string ServerHashForStorage(string clientHash)
        {
            // Em produção: leia de Environment.GetEnvironmentVariable("RPG_SERVER_SALT")
            // Nunca hardcode em repositório público.
            string serverSalt = System.Environment.GetEnvironmentVariable("RPG_SERVER_SALT")
                                ?? "TROQUE_ESTA_CHAVE_ANTES_DO_LAUNCH_USE_ENV_VAR";

            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(clientHash + serverSalt);
            byte[] hash  = sha256.ComputeHash(bytes);
            return System.BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
#endif
    }
}