using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using RPG.Data;

namespace RPG.Managers
{
    /// <summary>
    /// SaveManager v2 — Server-Only
    ///
    /// ATENÇÃO: Em servidor dedicado, este componente roda APENAS no servidor.
    /// Clientes NÃO acessam o SaveManager diretamente.
    ///
    /// Adicionado:
    ///   - TryCreateAccount com overload que aceita hash já pronto
    ///     (usado pelo ServerAuthManager quando o hash vem do cliente).
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

            try   { Directory.CreateDirectory(SavePath); }
            catch (Exception e)
            { Debug.LogError($"[SaveManager] Não foi possível criar diretório: {e.Message}"); }
        }

        // ── Conta ──────────────────────────────────────────────────────

        private string AccountFilePath(string username)
            => Path.Combine(SavePath, $"{username.ToLower().Trim()}.json");

        public bool AccountExists(string username)
            => File.Exists(AccountFilePath(username));

        public void SaveAccount(AccountData account)
        {
            if (account == null || string.IsNullOrWhiteSpace(account.Username)) return;
            account.LastLogin = DateTime.UtcNow.ToString("o");
            try
            {
                File.WriteAllText(AccountFilePath(account.Username),
                                  JsonUtility.ToJson(account, true));
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
            try   { return JsonUtility.FromJson<AccountData>(File.ReadAllText(path)); }
            catch (Exception e)
            {
                Debug.LogError($"[SaveManager] Erro ao carregar conta '{username}': {e.Message}");
                return null;
            }
        }

        // ── Autenticação ───────────────────────────────────────────────

        /// <summary>Recebe username e passwordHash (já em SHA-256).</summary>
        public AccountData TryLoginWithHash(string username, string passwordHash)
        {
            var account = LoadAccount(username);
            if (account == null) return null;
            return account.PasswordHash == passwordHash ? account : null;
        }

        /// <summary>
        /// Cria conta com hash já computado (vindo do cliente via rede).
        /// Parâmetro alreadyHashed = true pula o re-hash.
        /// </summary>
        public string TryCreateAccount(string username, string passwordOrHash, bool alreadyHashed = false)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length < 4)
                return "Username deve ter ao menos 4 caracteres.";
            if (AccountExists(username))
                return "Username já está em uso.";
            if (string.IsNullOrWhiteSpace(passwordOrHash) || passwordOrHash.Length < 4)
                return "Senha inválida.";

            string hash = alreadyHashed
                ? passwordOrHash
                : GameManager.HashPassword(passwordOrHash);

            var account = new AccountData
            {
                Username     = username.Trim(),
                PasswordHash = hash,
                Characters   = new List<CharacterData>()
            };
            SaveAccount(account);
            return null;
        }

        // ── Personagem ─────────────────────────────────────────────────

        public void SaveCharacter(AccountData account, CharacterData character)
        {
            if (account == null || character == null) return;
            int idx = account.Characters.FindIndex(c => c.CharacterId == character.CharacterId);
            if (idx >= 0) account.Characters[idx] = character;
            else          account.Characters.Add(character);
            SaveAccount(account);
        }

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
            var stats = ch.GetDerivedStats();
            ch.CurrentHP = stats.MaxHP;
            ch.CurrentMP = stats.MaxMP;

            SaveCharacter(account, ch);
            return null;
        }
    }
}
