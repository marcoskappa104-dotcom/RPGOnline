using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using RPG.Data;

namespace RPG.Managers
{
    /// <summary>
    /// SaveManager — persistência local em JSON.
    /// Simula um servidor: cada conta é salva em um arquivo separado.
    /// Troque por chamadas HTTP para ter um backend real.
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
            Directory.CreateDirectory(SavePath);
        }

        // ── Conta ─────────────────────────────────────────────────────────

        private string AccountFile(string username)
            => Path.Combine(SavePath, $"{username.ToLower()}.json");

        public bool AccountExists(string username)
            => File.Exists(AccountFile(username));

        public void SaveAccount(AccountData account)
        {
            account.LastLogin = DateTime.UtcNow.ToString("o");
            string json = JsonUtility.ToJson(account, true);
            File.WriteAllText(AccountFile(account.Username), json);
        }

        public AccountData LoadAccount(string username)
        {
            string path = AccountFile(username);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return JsonUtility.FromJson<AccountData>(json);
        }

        // ── Auth ──────────────────────────────────────────────────────────

        /// <summary>Retorna a conta se login ok, null caso contrário.</summary>
        public AccountData TryLogin(string username, string password)
        {
            var account = LoadAccount(username);
            if (account == null) return null;
            string hash = GameManager.HashPassword(password);
            return account.PasswordHash == hash ? account : null;
        }

        /// <summary>Cria conta nova. Retorna mensagem de erro ou null se ok.</summary>
        public string TryCreateAccount(string username, string password)
        {
            if (username.Length < 4)
                return "Username deve ter ao menos 4 caracteres.";
            if (AccountExists(username))
                return "Username já está em uso.";
            if (password.Length < 4)
                return "Senha deve ter ao menos 4 caracteres.";

            var account = new AccountData
            {
                Username     = username,
                PasswordHash = GameManager.HashPassword(password),
                Characters   = new List<CharacterData>()
            };
            SaveAccount(account);
            return null; // sem erro
        }

        // ── Personagem ────────────────────────────────────────────────────

        /// <summary>Adiciona ou atualiza personagem na conta e salva.</summary>
        public void SaveCharacter(AccountData account, CharacterData character)
        {
            int idx = account.Characters.FindIndex(c => c.CharacterId == character.CharacterId);
            if (idx >= 0) account.Characters[idx] = character;
            else          account.Characters.Add(character);
            SaveAccount(account);
        }

        /// <summary>Cria personagem novo. Retorna mensagem de erro ou null se ok.</summary>
        public string TryCreateCharacter(AccountData account, string name, CharacterRace race)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
                return "Nome do personagem deve ter ao menos 2 caracteres.";
            if (account.Characters.Count >= 5)
                return "Limite de 5 personagens por conta.";
            if (account.Characters.Exists(c =>
                string.Equals(c.CharacterName, name, StringComparison.OrdinalIgnoreCase)))
                return "Já existe um personagem com esse nome.";

            var ch = new CharacterData
            {
                CharacterId   = Guid.NewGuid().ToString(),
                CharacterName = name,
                Race          = race,
                Level         = 1,
                Experience    = 0,
                ExperienceToNextLevel = 100
            };
            ch.ApplyRaceBonus();

            // HP/MP iniciais cheios
            var stats = ch.GetDerivedStats();
            ch.CurrentHP = stats.MaxHP;
            ch.CurrentMP = stats.MaxMP;

            SaveCharacter(account, ch);
            return null;
        }
    }
}
