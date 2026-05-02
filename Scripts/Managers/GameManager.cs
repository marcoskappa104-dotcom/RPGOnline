using UnityEngine;
using UnityEngine.SceneManagement;
using RPG.Data;

namespace RPG.Managers
{
    /// <summary>
    /// GameManager — singleton que persiste entre cenas.
    /// Guarda referências globais: conta logada, personagem selecionado, etc.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        // ── Dados da sessão atual ──────────────────────────────────────────
        public AccountData   CurrentAccount   { get; private set; }
        public CharacterData SelectedCharacter { get; private set; }

        // ── Constantes de cenas ───────────────────────────────────────────
        public const string SCENE_LOGIN     = "LoginScene";
        public const string SCENE_CHARACTER = "CharacterScene";
        public const string SCENE_GAMEPLAY  = "GameplayScene";

        // ── Versão do jogo ────────────────────────────────────────────────
        public const string GAME_VERSION = "0.1.0-alpha";

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

        // ── Fluxo de navegação ────────────────────────────────────────────

        public void SetAccount(AccountData account)
        {
            CurrentAccount = account;
            Debug.Log($"[GameManager] Conta setada: {account.Username}");
        }

        public void SetSelectedCharacter(CharacterData character)
        {
            SelectedCharacter = character;
            Debug.Log($"[GameManager] Personagem selecionado: {character.CharacterName}");
        }

        public void GoToCharacterSelect()
        {
            SceneManager.LoadScene(SCENE_CHARACTER);
        }

        public void GoToGameplay()
        {
            if (SelectedCharacter == null)
            {
                Debug.LogError("[GameManager] Nenhum personagem selecionado!");
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

        // ── Utilidades ────────────────────────────────────────────────────

        /// <summary>Hash MD5 simples para senha (use bcrypt em produção)</summary>
        public static string HashPassword(string password)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] bytes  = System.Text.Encoding.UTF8.GetBytes(password);
            byte[] hash   = md5.ComputeHash(bytes);
            return System.BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
}
