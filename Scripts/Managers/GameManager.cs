using UnityEngine;
using UnityEngine.SceneManagement;
using RPG.Data;

namespace RPG.Managers
{
    /// <summary>
    /// GameManager — singleton persistente entre cenas.
    /// Guarda a sessão atual: conta logada e personagem selecionado.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        // ── Sessão atual ───────────────────────────────────────────────────
        public AccountData   CurrentAccount    { get; private set; }
        public CharacterData SelectedCharacter { get; private set; }

        // ── Constantes de cenas ────────────────────────────────────────────
        public const string SCENE_LOGIN     = "LoginScene";
        public const string SCENE_CHARACTER = "CharacterScene";
        public const string SCENE_GAMEPLAY  = "GameplayScene";
        public const string GAME_VERSION    = "0.1.0-alpha";

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log($"[GameManager] Iniciado — versão {GAME_VERSION}");
        }

        // ── Sessão ─────────────────────────────────────────────────────────

        public void SetAccount(AccountData account)
        {
            CurrentAccount = account;
            Debug.Log($"[GameManager] Conta setada: {account?.Username}");
        }

        public void SetSelectedCharacter(CharacterData character)
        {
            SelectedCharacter = character;
            Debug.Log($"[GameManager] Personagem selecionado: {character?.CharacterName}");
        }

        // ── Navegação ──────────────────────────────────────────────────────

        public void GoToCharacterSelect()
        {
            if (CurrentAccount == null)
            {
                Debug.LogError("[GameManager] GoToCharacterSelect sem conta logada!");
                return;
            }
            SceneManager.LoadScene(SCENE_CHARACTER);
        }

        public void GoToGameplay()
        {
            if (SelectedCharacter == null)
            {
                Debug.LogError("[GameManager] GoToGameplay sem personagem selecionado!");
                return;
            }
            SceneManager.LoadScene(SCENE_GAMEPLAY);
        }

        public void Logout()
        {
            CurrentAccount    = null;
            SelectedCharacter = null;
            SceneManager.LoadScene(SCENE_LOGIN);
        }

        // ── Hash de senha ──────────────────────────────────────────────────

        /// <summary>
        /// Hash SHA-256 da senha para armazenamento local.
        /// ATENÇÃO: Em produção com banco de dados real, use bcrypt/Argon2
        /// com salt no servidor — nunca armazene senhas em texto puro ou MD5.
        /// </summary>
        public static string HashPassword(string password)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] bytes     = System.Text.Encoding.UTF8.GetBytes(password);
            byte[] hash      = sha256.ComputeHash(bytes);
            return System.BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
}