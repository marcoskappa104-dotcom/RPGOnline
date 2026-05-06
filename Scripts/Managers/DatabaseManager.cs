// ============================================================
// DatabaseManager.cs — RPG Online
// Usa: sqlite-net-pcl (SQLite.cs + SQLite3.cs — arquivo único)
//
// IMPORTANTE: Antes de compilar, siga o GUIA_INSTALACAO.md
// ============================================================

using UnityEngine;
using SQLite;                  // namespace do sqlite-net
using System.Collections.Generic;
using System;
using RPG.Data;

namespace RPG.Managers
{
    // ── Tabelas mapeadas para o SQLite ─────────────────────────────────────

    /// <summary>Tabela: accounts</summary>
    [Table("accounts")]
    public class AccountRow
    {
        [PrimaryKey]
        [Column("username")]
        public string Username { get; set; }

        [Column("password_hash"), NotNull]
        public string PasswordHash { get; set; }

        [Column("created_at"), NotNull]
        public string CreatedAt { get; set; }

        [Column("last_login")]
        public string LastLogin { get; set; }
    }

    /// <summary>Tabela: characters — todos os dados do personagem em colunas separadas.</summary>
    [Table("characters")]
    public class CharacterRow
    {
        [PrimaryKey]
        [Column("character_id")]
        public string CharacterId { get; set; }

        [Column("username"), NotNull, Indexed]
        public string Username { get; set; }

        [Column("character_name"), NotNull, Unique]
        public string CharacterName { get; set; }

        [Column("race")]
        public int Race { get; set; }

        [Column("level")]
        public int Level { get; set; } = 1;

        [Column("experience")]
        public long Experience { get; set; } = 0;

        [Column("exp_to_next")]
        public long ExpToNext { get; set; } = 100;

        [Column("current_hp")]
        public float CurrentHP { get; set; } = 100f;

        [Column("current_mp")]
        public float CurrentMP { get; set; } = 50f;

        [Column("pos_x")]
        public float PosX { get; set; } = 0f;

        [Column("pos_y")]
        public float PosY { get; set; } = 1f;

        [Column("pos_z")]
        public float PosZ { get; set; } = 0f;

        [Column("current_map")]
        public string CurrentMap { get; set; } = "World_01";

        [Column("free_points")]
        public int FreePoints { get; set; } = 0;

        [Column("alloc_str")]
        public int AllocSTR { get; set; } = 0;

        [Column("alloc_agi")]
        public int AllocAGI { get; set; } = 0;

        [Column("alloc_vit")]
        public int AllocVIT { get; set; } = 0;

        [Column("alloc_dex")]
        public int AllocDEX { get; set; } = 0;

        [Column("alloc_int")]
        public int AllocINT { get; set; } = 0;

        [Column("alloc_luk")]
        public int AllocLUK { get; set; } = 0;
    }

    /// <summary>Tabela: inventory (preparado para o futuro)</summary>
    [Table("inventory")]
    public class InventoryRow
    {
        [PrimaryKey, AutoIncrement]
        [Column("id")]
        public int Id { get; set; }

        [Column("character_id"), NotNull, Indexed]
        public string CharacterId { get; set; }

        [Column("item_id"), NotNull]
        public string ItemId { get; set; }

        [Column("quantity")]
        public int Quantity { get; set; } = 1;

        [Column("slot_index")]
        public int SlotIndex { get; set; } = -1;

        [Column("is_equipped")]
        public bool IsEquipped { get; set; } = false;
    }

    /// <summary>Tabela: economy_log (analytics e balanceamento)</summary>
    [Table("economy_log")]
    public class EconomyLogRow
    {
        [PrimaryKey, AutoIncrement]
        [Column("id")]
        public int Id { get; set; }

        [Column("character_id"), NotNull]
        public string CharacterId { get; set; }

        [Column("event_type"), NotNull]
        public string EventType { get; set; }

        [Column("value")]
        public float Value { get; set; }

        [Column("timestamp"), NotNull]
        public string Timestamp { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════
    // DatabaseManager
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DatabaseManager v2 — usa sqlite-net-pcl (SQLite.cs).
    ///
    /// Substitui SaveManager.cs completamente.
    /// Todas as operações são síncronas e seguras para o servidor dedicado.
    ///
    /// REMOVE DO PROJETO: SaveManager.cs
    /// ADICIONE AO PROJETO: SQLite.cs (ver GUIA_INSTALACAO.md)
    /// </summary>
    public class DatabaseManager : MonoBehaviour
    {
        public static DatabaseManager Instance { get; private set; }

        private SQLiteConnection _db;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            InitializeDatabase();
        }

        private void OnDestroy()
        {
            _db?.Close();
            _db = null;
        }

private void InitializeDatabase()
{
    try
    {
        string dbPath = System.IO.Path.Combine(
            Application.persistentDataPath, "rpg_server.db");

        Debug.Log($"[DatabaseManager] Banco em: {dbPath}");

        _db = new SQLiteConnection(dbPath);

        // PRAGMAs (corrigido)
        _db.ExecuteScalar<string>("PRAGMA journal_mode=WAL;");
        _db.Execute("PRAGMA foreign_keys = ON;");

        // Tabelas
        _db.CreateTable<AccountRow>();
        _db.CreateTable<CharacterRow>();
        _db.CreateTable<InventoryRow>();
        _db.CreateTable<EconomyLogRow>();

        Debug.Log("[DatabaseManager] Banco inicializado com sucesso.");
    }
    catch (Exception e)
    {
        Debug.LogError($"[DatabaseManager] ERRO ao inicializar banco:\n{e}");
    }
}
        // ══════════════════════════════════════════════════════════════════
        // CONTAS
        // ══════════════════════════════════════════════════════════════════

        public bool AccountExists(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            try
            {
                return _db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM accounts WHERE LOWER(username) = LOWER(?)",
                    username.Trim()) > 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] AccountExists erro: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Cria conta. Retorna null se sucesso, mensagem de erro se falhar.
        /// passwordHash deve vir em SHA-256 do cliente.
        /// </summary>
        public string TryCreateAccount(string username, string passwordHash)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 4)
                return "Username deve ter ao menos 4 caracteres.";
            if (string.IsNullOrWhiteSpace(passwordHash))
                return "Senha inválida.";
            if (AccountExists(username))
                return "Username já está em uso.";

            try
            {
                _db.Insert(new AccountRow
                {
                    Username     = username.Trim().ToLower(),
                    PasswordHash = passwordHash,
                    CreatedAt    = DateTime.UtcNow.ToString("o"),
                    LastLogin    = null
                });

                Debug.Log($"[DB] Conta criada: {username}");
                return null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] TryCreateAccount erro: {e.Message}");
                return "Erro interno ao criar conta.";
            }
        }

        /// <summary>
        /// Autentica. Retorna AccountData populado se ok, null se falhar.
        /// Atualiza last_login automaticamente.
        /// </summary>
        public AccountData TryLoginWithHash(string username, string passwordHash)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordHash))
                return null;

            try
            {
                var row = _db.FindWithQuery<AccountRow>(
                    "SELECT * FROM accounts WHERE LOWER(username) = LOWER(?) AND password_hash = ?",
                    username.Trim(), passwordHash);

                if (row == null) return null;

                // Atualiza last_login
                _db.Execute(
                    "UPDATE accounts SET last_login = ? WHERE username = ?",
                    DateTime.UtcNow.ToString("o"), row.Username);

                return new AccountData
                {
                    Username     = row.Username,
                    PasswordHash = row.PasswordHash,
                    Characters   = LoadCharacters(row.Username)
                };
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] TryLoginWithHash erro: {e.Message}");
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // PERSONAGENS
        // ══════════════════════════════════════════════════════════════════

        public List<CharacterData> LoadCharacters(string username)
        {
            var list = new List<CharacterData>();
            if (string.IsNullOrWhiteSpace(username)) return list;

            try
            {
                var rows = _db.Query<CharacterRow>(
                    "SELECT * FROM characters WHERE LOWER(username) = LOWER(?)",
                    username.Trim());

                foreach (var row in rows)
                    list.Add(RowToCharacterData(row));
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] LoadCharacters erro: {e.Message}");
            }

            return list;
        }

        public CharacterData LoadCharacter(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return null;

            try
            {
                var row = _db.Find<CharacterRow>(characterId);
                return row != null ? RowToCharacterData(row) : null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] LoadCharacter erro: {e.Message}");
                return null;
            }
        }
/// <summary>
/// Carrega personagem verificando ownership em uma única query.
/// Mais seguro e eficiente que LoadCharacter + LoadCharacters separados.
/// </summary>
public CharacterData LoadCharacterForAccount(string characterId, string username)
{
    if (string.IsNullOrWhiteSpace(characterId) || string.IsNullOrWhiteSpace(username))
        return null;

    try
    {
        var row = _db.FindWithQuery<CharacterRow>(
            "SELECT * FROM characters WHERE character_id = ? AND LOWER(username) = LOWER(?)",
            characterId, username.Trim());

        return row != null ? RowToCharacterData(row) : null;
    }
    catch (Exception e)
    {
        Debug.LogError($"[DB] LoadCharacterForAccount erro: {e.Message}");
        return null;
    }
}
        /// <summary>
        /// Cria personagem. Retorna null se sucesso, mensagem de erro se falhar.
        /// </summary>
        public string TryCreateCharacter(string username, string name, CharacterRace race)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
                return "Nome deve ter ao menos 2 caracteres.";

            try
            {
                // Limite de personagens por conta
                int count = _db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM characters WHERE LOWER(username) = LOWER(?)",
                    username.Trim());
                if (count >= 5)
                    return "Limite de 5 personagens por conta atingido.";

                // Nome único global
                int nameCount = _db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM characters WHERE LOWER(character_name) = LOWER(?)",
                    name.Trim());
                if (nameCount > 0)
                    return "Já existe um personagem com esse nome.";

                // Cria o CharacterData para calcular HP/MP iniciais
                var ch = new CharacterData
                {
                    CharacterId           = Guid.NewGuid().ToString(),
                    CharacterName         = name.Trim(),
                    Race                  = race,
                    Level                 = 1,
                    Experience            = 0,
                    ExperienceToNextLevel = 100,
                    CurrentMap            = "World_01",
                    BaseAttributes        = new BaseAttributes { STR=10, AGI=10, VIT=10, DEX=10, INT=10, LUK=10 },
                    EquipmentBonuses      = new EquipmentBonuses()
                };
                var stats    = ch.GetDerivedStats();
                ch.CurrentHP = stats.MaxHP;
                ch.CurrentMP = stats.MaxMP;

                _db.Insert(new CharacterRow
                {
                    CharacterId   = ch.CharacterId,
                    Username      = username.Trim().ToLower(),
                    CharacterName = ch.CharacterName,
                    Race          = (int)ch.Race,
                    Level         = ch.Level,
                    Experience    = ch.Experience,
                    ExpToNext     = ch.ExperienceToNextLevel,
                    CurrentHP     = ch.CurrentHP,
                    CurrentMP     = ch.CurrentMP,
                    PosX          = 0f,
                    PosY          = 1f,
                    PosZ          = 0f,
                    CurrentMap    = ch.CurrentMap,
                    FreePoints    = 0,
                    AllocSTR = 0, AllocAGI = 0, AllocVIT = 0,
                    AllocDEX = 0, AllocINT = 0, AllocLUK = 0
                });

                Debug.Log($"[DB] Personagem criado: {ch.CharacterName} ({race}) para {username}");
                return null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] TryCreateCharacter erro: {e.Message}");
                return "Erro interno ao criar personagem.";
            }
        }

        /// <summary>
        /// Salva todos os campos de progressão no banco.
        /// Chamado pelo NetworkPlayer.ServerSaveCharacter().
        /// Operação de UPDATE puro — sem reler nada do disco.
        /// </summary>
        public void SaveCharacter(CharacterData ch, string username)
        {
            if (ch == null || string.IsNullOrWhiteSpace(ch.CharacterId)) return;

            try
            {
                _db.Execute(@"
                    UPDATE characters SET
                        level        = ?,
                        experience   = ?,
                        exp_to_next  = ?,
                        current_hp   = ?,
                        current_mp   = ?,
                        pos_x        = ?,
                        pos_y        = ?,
                        pos_z        = ?,
                        current_map  = ?,
                        free_points  = ?,
                        alloc_str    = ?,
                        alloc_agi    = ?,
                        alloc_vit    = ?,
                        alloc_dex    = ?,
                        alloc_int    = ?,
                        alloc_luk    = ?
                    WHERE character_id = ? AND LOWER(username) = LOWER(?)",
                    ch.Level,
                    ch.Experience,
                    ch.ExperienceToNextLevel,
                    ch.CurrentHP,
                    ch.CurrentMP,
                    ch.PosX,
                    ch.PosY,
                    ch.PosZ,
                    ch.CurrentMap ?? "World_01",
                    ch.FreeAttributePoints,
                    ch.AllocatedSTR,
                    ch.AllocatedAGI,
                    ch.AllocatedVIT,
                    ch.AllocatedDEX,
                    ch.AllocatedINT,
                    ch.AllocatedLUK,
                    ch.CharacterId,
                    username.Trim());
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] SaveCharacter erro ({ch?.CharacterName}): {e.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO
        // ══════════════════════════════════════════════════════════════════

        public List<InventoryRow> LoadInventory(string characterId)
        {
            try
            {
                return _db.Query<InventoryRow>(
                    "SELECT * FROM inventory WHERE character_id = ?", characterId);
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] LoadInventory erro: {e.Message}");
                return new List<InventoryRow>();
            }
        }

        public void AddItem(string characterId, string itemId, int quantity = 1, int slot = -1)
        {
            try
            {
                _db.Insert(new InventoryRow
                {
                    CharacterId = characterId,
                    ItemId      = itemId,
                    Quantity    = quantity,
                    SlotIndex   = slot,
                    IsEquipped  = false
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] AddItem erro: {e.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // LOG DE ECONOMIA
        // ══════════════════════════════════════════════════════════════════

        public void LogEconomy(string characterId, string eventType, float value)
        {
            try
            {
                _db.Insert(new EconomyLogRow
                {
                    CharacterId = characterId,
                    EventType   = eventType,
                    Value       = value,
                    Timestamp   = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] LogEconomy erro: {e.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════════════

        private CharacterData RowToCharacterData(CharacterRow row)
        {
            return new CharacterData
            {
                CharacterId           = row.CharacterId,
                CharacterName         = row.CharacterName,
                Race                  = (CharacterRace)row.Race,
                Level                 = row.Level,
                Experience            = row.Experience,
                ExperienceToNextLevel = row.ExpToNext,
                CurrentHP             = row.CurrentHP,
                CurrentMP             = row.CurrentMP,
                PosX                  = row.PosX,
                PosY                  = row.PosY,
                PosZ                  = row.PosZ,
                CurrentMap            = row.CurrentMap ?? "World_01",
                FreeAttributePoints   = row.FreePoints,
                AllocatedSTR          = row.AllocSTR,
                AllocatedAGI          = row.AllocAGI,
                AllocatedVIT          = row.AllocVIT,
                AllocatedDEX          = row.AllocDEX,
                AllocatedINT          = row.AllocINT,
                AllocatedLUK          = row.AllocLUK,
                BaseAttributes        = new BaseAttributes { STR=10, AGI=10, VIT=10, DEX=10, INT=10, LUK=10 },
                EquipmentBonuses      = new EquipmentBonuses()
            };
        }
    }
}