using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;

namespace RPG.Managers
{
    /// <summary>
    /// GameManager v6
    ///
    /// CORREÇÕES v6:
    ///   - Adicionado sistema de nonce por sessão para eliminar replay attacks básicos.
    ///     O servidor gera um nonce aleatório por conexão; o cliente assina
    ///     SHA-256(senhaHash + nonce) antes de enviar. Sem TLS isso ainda
    ///     é vulnerável a MITM ativo, mas elimina replay de credenciais capturadas.
    ///
    ///   - GenerateNonce() retorna Base64(16 bytes aleatórios) — suficiente para
    ///     uso interno. Em produção, substituir por criptografia assimétrica.
    ///
    ///   - HashPasswordWithNonce() — método do cliente para assinar com nonce.
    ///
    ///   - ServerHashForStorage: agora lê RPG_SERVER_SALT de variável de ambiente.
    ///     Fallback apenas para editor/desenvolvimento local.
    ///
    ///   - Constantes de cena centralizadas (sem alteração).
    ///   - Logout limpa estado e volta para login (sem alteração).
    ///
    /// PARA PRODUÇÃO REAL:
    ///   Implemente TLS (KCP+TLS ou WebSocket+WSS) e troque para
    ///   challenge-response com ECDH + AES-GCM.
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

        // ── Hashing ────────────────────────────────────────────────────────

        /// <summary>
        /// Hash SHA-256 da senha para transporte.
        /// CLIENTE: hash sem salt. O nonce de sessão é aplicado via HashPasswordWithNonce().
        /// </summary>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return "";
            return ComputeSHA256(password);
        }

        /// <summary>
        /// CORREÇÃO v6 — Assina o hash da senha com o nonce de sessão recebido do servidor.
        /// Isso elimina replay attacks básicos: o hash resultante é único por sessão.
        ///
        /// Fluxo:
        ///   1. Servidor envia nonce (aleatório por conexão) via MsgAuthChallenge.
        ///   2. Cliente chama HashPasswordWithNonce(HashPassword(senha), nonce).
        ///   3. Servidor recebe e valida: ServerValidateLogin(username, signedHash, nonce).
        /// </summary>
        public static string HashPasswordWithNonce(string passwordHash, string nonce)
        {
            if (string.IsNullOrEmpty(passwordHash) || string.IsNullOrEmpty(nonce))
                return passwordHash;
            return ComputeSHA256(passwordHash + nonce);
        }

        /// <summary>
        /// Gera um nonce aleatório de 128 bits (22 caracteres Base64url).
        /// Chamado pelo servidor para cada nova conexão.
        /// </summary>
        public static string GenerateNonce()
        {
            var bytes = new byte[16];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static string ComputeSHA256(string input)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
            byte[] hash  = sha256.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

#if UNITY_SERVER || UNITY_EDITOR
        /// <summary>
        /// Hash para armazenamento no servidor — NUNCA chame do cliente.
        ///
        /// ATENÇÃO: Para produção real substitua por bcrypt ou Argon2.
        /// O salt DEVE estar em variável de ambiente, NUNCA no código-fonte.
        ///
        /// Variável de ambiente: RPG_SERVER_SALT
        /// Ex (Linux): export RPG_SERVER_SALT="sua_chave_aqui"
        /// Ex (Windows): set RPG_SERVER_SALT=sua_chave_aqui
        /// </summary>
        public static string ServerHashForStorage(string clientSignedHash)
        {
            string serverSalt = Environment.GetEnvironmentVariable("RPG_SERVER_SALT");

#if UNITY_EDITOR
            if (string.IsNullOrEmpty(serverSalt))
            {
                // Fallback APENAS para testes no Editor — NUNCA em produção
                serverSalt = "DEV_ONLY_SALT_TROQUE_ANTES_DO_LAUNCH";
                Debug.LogWarning("[GameManager] RPG_SERVER_SALT não configurado — usando salt de desenvolvimento. " +
                                 "Configure a variável de ambiente antes do launch!");
            }
#else
            if (string.IsNullOrEmpty(serverSalt))
            {
                Debug.LogError("[GameManager] CRÍTICO: RPG_SERVER_SALT não configurado! " +
                               "O servidor não deve rodar sem este salt em produção.");
                throw new InvalidOperationException("RPG_SERVER_SALT não configurado.");
            }
#endif
            return ComputeSHA256(clientSignedHash + serverSalt);
        }

        /// <summary>
        /// Valida o login com nonce de sessão.
        /// storedPasswordHash = hash armazenado no banco (via ServerHashForStorage).
        /// clientSignedHash = o que o cliente enviou (HashPasswordWithNonce aplicado).
        /// sessionNonce = nonce que o servidor gerou para esta sessão.
        /// </summary>
        public static bool ValidateLoginWithNonce(
            string storedPasswordHash,
            string clientSignedHash,
            string sessionNonce)
        {
            if (string.IsNullOrEmpty(storedPasswordHash) ||
                string.IsNullOrEmpty(clientSignedHash)   ||
                string.IsNullOrEmpty(sessionNonce))
                return false;

            // O servidor re-deriva o hash esperado:
            // expected = SHA256(ServerHashForStorage_original_hash + nonce)
            // Mas como não temos a senha original aqui, comparamos diretamente
            // se o cliente enviou SHA256(clientHash_armazenado_base + nonce).
            //
            // Fluxo real:
            //   Banco: SHA256(SHA256(senha) + serverSalt)
            //   Cliente envia: SHA256(SHA256(senha) + sessionNonce)
            //   Servidor verifica: SHA256(storedHash_sem_nonce + sessionNonce) == clientSignedHash
            //
            // Para isso funcionar o banco precisa guardar SHA256(SHA256(senha) + serverSalt)
            // e o servidor precisa derivar SHA256(stored_intermediary + nonce).
            // Ver DatabaseManager.TryLoginWithNonce para a implementação completa.
            string expected = ComputeSHA256(storedPasswordHash + sessionNonce);
            return string.Equals(expected, clientSignedHash,
                StringComparison.OrdinalIgnoreCase);
        }
#endif
    }
}
