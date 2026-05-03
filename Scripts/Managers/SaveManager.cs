using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using RPG.Data;

namespace RPG.Managers
{
    /// <summary>
    /// SaveManager — persistência local em JSON por arquivo de conta.
    ///
    /// ARQUITETURA:
    ///   Cada conta é salva em "accounts/{username_lowercase}.json".
    ///   O servidor dedicado usa Application.persistentDataPath, que em
    ///   Windows Server aponta para AppData\LocalLow do processo.
    ///
    /// PRODUÇÃO:
    ///   Substitua SaveAccount / LoadAccount por chamadas HTTP a um backend
    ///   (ex: Node.js + MySQL). O resto do código não precisa mudar.
    /// </summary>
    public class SaveManager : MonoBehaviour
    {
        public static SaveManager Instance { get; private set; }

        private string SavePath => Path.Combine(Application.persistentDataPath, "accounts");

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            try
            {
                Directory.CreateDirectory(SavePath);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveManager] Não foi possível criar diretório de saves: {e.Message}");
            }
        }

        // ── Conta ──────────────────────────────────────────────────────────

        private string AccountFilePath(string username)
            => Path.Combine(SavePath, $"{username.ToLower().Trim()}.json");

        public bool AccountExists(string username)
            => File.Exists(AccountFilePath(username));

        public void SaveAccount(AccountData account)
        {
            if (account == null || string.IsNullOrWhiteSpace(account.Username))
            {
                Debug.LogError("[SaveManager] SaveAccount: conta inválida.");
                return;
            }

            account.LastLogin = DateTime.UtcNow.ToString("o");
            try
            {
                string json = JsonUtility.ToJson(account, true);
                File.WriteAllText(AccountFilePath(account.Username), json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveManager] Erro ao salvar conta '{account.Username}': {e.Message}");
            }
        }

        public AccountData LoadAccount(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            string path = AccountFilePath(username);
            if (!File.Exists(path)) return null;

            try
            {
                string json = File.ReadAllText(path);
                return JsonUtility.FromJson<AccountData>(json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveManager] Erro ao carregar conta '{username}': {e.Message}");
                return null;
            }
        }

        // ── Autenticação ───────────────────────────────────────────────────

        /// <summary>Retorna a conta se o login for válido, null caso contrário.</summary>
        public AccountData TryLogin(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return null;

            var account = LoadAccount(username);
            if (account == null) return null;

            string hash = GameManager.HashPassword(password);
            return account.PasswordHash == hash ? account : null;
        }

        /// <summary>Cria conta nova. Retorna mensagem de erro ou null se ok.</summary>
        public string TryCreateAccount(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length < 4)
                return "Username deve ter ao menos 4 caracteres.";
            if (AccountExists(username))
                return "Username já está em uso.";
            if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
                return "Senha deve ter ao menos 4 caracteres.";

            var account = new AccountData
            {
                Username     = username.Trim(),
                PasswordHash = GameManager.HashPassword(password),
                Characters   = new List<CharacterData>()
            };

            SaveAccount(account);
            return null;
        }

        // ── Personagem ─────────────────────────────────────────────────────

        /// <summary>
        /// Salva ou atualiza um personagem na conta correta.
        /// Recebe a AccountData completa para evitar o bug de usar CharacterName como Username.
        /// </summary>
        public void SaveCharacter(AccountData account, CharacterData character)
        {
            if (account == null || character == null)
            {
                Debug.LogError("[SaveManager] SaveCharacter: argumentos inválidos.");
                return;
            }

            int idx = account.Characters.FindIndex(c => c.CharacterId == character.CharacterId);
            if (idx >= 0)
                account.Characters[idx] = character;
            else
                account.Characters.Add(character);

            SaveAccount(account);
        }

        /// <summary>
        /// Cria personagem novo na conta. Retorna mensagem de erro ou null se ok.
        /// </summary>
        public string TryCreateCharacter(AccountData account, string name, CharacterRace race)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
                return "Nome deve ter ao menos 2 caracteres.";
            if (account.Characters.Count >= 5)
                return "Limite de 5 personagens por conta atingido.";
            if (account.Characters.Exists(c =>
                    string.Equals(c.CharacterName, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return "Já existe um personagem com esse nome.";

            var ch = new CharacterData
            {
                CharacterId           = Guid.NewGuid().ToString(),
                CharacterName         = name.Trim(),
                Race                  = race,
                Level                 = 1,
                Experience            = 0,
                ExperienceToNextLevel = 100
            };

            // HP/MP iniciais cheios
            var stats = ch.GetDerivedStats();
            ch.CurrentHP = stats.MaxHP;
            ch.CurrentMP = stats.MaxMP;

            SaveCharacter(account, ch);
            return null;
        }
    }
}