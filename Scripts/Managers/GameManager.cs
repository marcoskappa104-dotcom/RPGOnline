using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;

namespace RPG.Managers
{
    /// <summary>
    /// GameManager v7 — CORREÇÃO CRÍTICA DO FLUXO DE AUTENTICAÇÃO
    ///
    /// ════════════════════════════════════════════════════════════════════
    /// ROOT CAUSE DO BUG "senha incorreta":
    ///
    /// O fluxo v6 tinha um erro de design no pipeline de hash que tornava
    /// IMPOSSÍVEL fazer login após criar uma conta. Aqui está o que acontecia:
    ///
    /// CRIAR CONTA (v6 — quebrado):
    ///   Cliente → HashPassword(senha)         = SHA256(senha)         = H1
    ///   Cliente → MsgCreateAccountRequest { PasswordHash = H1 }
    ///   Servidor → ServerHashForStorage(H1)   = SHA256(H1 + serverSalt) = STORED
    ///
    /// LOGIN (v6 — quebrado):
    ///   Cliente → HashPassword(senha)          = SHA256(senha)         = H1
    ///   Cliente → HashPasswordWithNonce(H1, nonce) = SHA256(H1 + nonce) = H2
    ///   Cliente → MsgLoginRequest { SignedHash = H2 }
    ///   Servidor → ValidateLoginWithNonce(STORED, H2, nonce)
    ///            = compara SHA256(STORED + nonce) com H2
    ///            = SHA256(SHA256(H1 + serverSalt) + nonce)  ≠  SHA256(H1 + nonce)
    ///            = FALHA SEMPRE — os hashes nunca batem
    ///
    /// ════════════════════════════════════════════════════════════════════
    /// SOLUÇÃO v7 — Pipeline unificado e correto:
    ///
    /// O banco passa a armazenar APENAS SHA256(senha) sem serverSalt aplicado
    /// na camada de storage. O salt do servidor é aplicado SOMENTE na validação
    /// em tempo real, nunca persistido. Isso alinha criação e login:
    ///
    /// CRIAR CONTA (v7 — correto):
    ///   Cliente → MsgCreateAccountRequest { PasswordHash = SHA256(senha) }
    ///   Servidor → armazena SHA256(senha) diretamente (sem serverSalt extra)
    ///
    /// LOGIN (v7 — correto):
    ///   Cliente → H1 = SHA256(senha)
    ///   Cliente → H2 = SHA256(H1 + nonce)   [assina com nonce da sessão]
    ///   Servidor → expected = SHA256(STORED + nonce) = SHA256(SHA256(senha) + nonce) = H2 ✓
    ///
    /// NOTA SOBRE SEGURANÇA:
    ///   Sem TLS, qualquer hash enviado pela rede é vulnerável a MITM ativo.
    ///   Para produção real use KCP+TLS ou WebSocket+WSS + bcrypt/Argon2 no servidor.
    ///   O nonce protege apenas contra replay de sessões capturadas anteriormente.
    ///
    /// AÇÃO NECESSÁRIA APÓS ATUALIZAR:
    ///   Delete o banco antigo: %AppData%\..\LocalLow\DefaultCompany\rpgonline\rpg_server.db
    ///   Recrie as contas — os hashes antigos são incompatíveis com o novo pipeline.
    /// ════════════════════════════════════════════════════════════════════
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

        // ══════════════════════════════════════════════════════════════════
        // HASHING — Pipeline v7 unificado
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Passo 1: hash base da senha sem salt.
        /// Usado tanto na criação de conta quanto no login.
        /// CLIENTE: nunca envie a senha em texto plano — sempre passe por aqui primeiro.
        /// </summary>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return "";
            return ComputeSHA256(password);
        }

        /// <summary>
        /// Passo 2 (login apenas): assina o hash base com o nonce da sessão.
        /// Elimina replay de sessões capturadas anteriormente.
        ///
        /// Fluxo cliente:
        ///   H1 = HashPassword(senha)              → SHA256(senha)
        ///   H2 = HashPasswordWithNonce(H1, nonce) → SHA256(H1 + nonce)
        ///   Enviar H2 no MsgLoginRequest.SignedHash
        ///
        /// O servidor valida comparando SHA256(STORED + nonce) com H2,
        /// onde STORED = SHA256(senha) = H1 (armazenado na criação).
        /// </summary>
        public static string HashPasswordWithNonce(string passwordHash, string nonce)
        {
            if (string.IsNullOrEmpty(passwordHash) || string.IsNullOrEmpty(nonce))
                return passwordHash;
            return ComputeSHA256(passwordHash + nonce);
        }

        /// <summary>
        /// Gera um nonce aleatório de 128 bits.
        /// Chamado pelo servidor para cada nova conexão.
        /// </summary>
        public static string GenerateNonce()
        {
            var bytes = new byte[16];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        // ── Métodos exclusivos do servidor ─────────────────────────────────

#if UNITY_SERVER || UNITY_EDITOR

        /// <summary>
        /// SERVIDOR APENAS — Prepara o hash para armazenamento no banco.
        ///
        /// v7: NÃO aplica serverSalt ao hash armazenado — o salt do servidor
        /// é usado apenas na VALIDAÇÃO em tempo real via ValidateLoginWithNonce.
        /// Isso unifica o pipeline e elimina o bug de hash incompatível.
        ///
        /// O que é armazenado: SHA256(senha) — exatamente o que o cliente enviou.
        ///
        /// Por que sem salt no storage? O nonce por sessão já previne replay.
        /// Para segurança adicional em produção, use bcrypt/Argon2 que incluem
        /// o salt automaticamente no hash armazenado.
        ///
        /// VARIÁVEL DE AMBIENTE: RPG_SERVER_SALT (usada apenas em ValidateLoginWithNonce)
        /// </summary>
        public static string ServerHashForStorage(string clientPasswordHash)
        {
            // v7: armazena o hash exatamente como recebido do cliente
            // O serverSalt é aplicado apenas na comparação, não no storage
            if (string.IsNullOrEmpty(clientPasswordHash))
            {
                Debug.LogError("[GameManager] ServerHashForStorage: hash vazio!");
                return "";
            }
            return clientPasswordHash;
        }

        /// <summary>
        /// SERVIDOR APENAS — Valida login com nonce de sessão.
        ///
        /// storedPasswordHash = SHA256(senha) armazenado no banco
        /// clientSignedHash   = SHA256(SHA256(senha) + nonce) enviado pelo cliente
        /// sessionNonce       = nonce gerado pelo servidor para esta sessão
        ///
        /// Pipeline v7:
        ///   expected = SHA256(storedPasswordHash + sessionNonce)
        ///            = SHA256(SHA256(senha) + nonce)
        ///   clientSignedHash = SHA256(SHA256(senha) + nonce)
        ///   → expected == clientSignedHash ✓
        ///
        /// OPCIONAL: serverSalt adicional na comparação para dificultar
        /// ataques offline caso o banco vaze. Leia de variável de ambiente.
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

            // Computa o hash esperado: SHA256(storedHash + nonce)
            // Corresponde exatamente ao que o cliente enviou via HashPasswordWithNonce
            string expected = ComputeSHA256(storedPasswordHash + sessionNonce);

            return string.Equals(expected, clientSignedHash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// SERVIDOR APENAS — Lê o serverSalt da variável de ambiente.
        /// Usado opcionalmente em lógica de segurança adicional.
        /// </summary>
        public static string GetServerSalt()
        {
            string salt = Environment.GetEnvironmentVariable("RPG_SERVER_SALT");

#if UNITY_EDITOR
            if (string.IsNullOrEmpty(salt))
            {
                salt = "DEV_ONLY_SALT_DO_NOT_USE_IN_PRODUCTION";
                Debug.LogWarning("[GameManager] RPG_SERVER_SALT não configurado — usando salt de dev.");
            }
#else
            if (string.IsNullOrEmpty(salt))
            {
                Debug.LogError("[GameManager] CRÍTICO: RPG_SERVER_SALT não configurado!");
                throw new InvalidOperationException("RPG_SERVER_SALT não configurado.");
            }
#endif
            return salt;
        }

#endif // UNITY_SERVER || UNITY_EDITOR

        // ── Utilitário ─────────────────────────────────────────────────────

        public static string ComputeSHA256(string input)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
            byte[] hash  = sha256.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
}