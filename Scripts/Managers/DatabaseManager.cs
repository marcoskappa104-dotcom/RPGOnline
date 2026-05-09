using UnityEngine;
using System.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.Threading;
using RPG.Data;

#if UNITY_SERVER
using SQLite;
#endif

namespace RPG.Managers
{
    // ══════════════════════════════════════════════════════════════════════
    // Tabelas SQLite — só compilam no servidor dedicado
    // ══════════════════════════════════════════════════════════════════════

#if UNITY_SERVER
    [Table("accounts")]
    public class AccountRow
    {
        [PrimaryKey][Column("username")]
        public string Username { get; set; }

        [Column("password_hash"), NotNull]
        public string PasswordHash { get; set; }

        [Column("created_at"), NotNull]
        public string CreatedAt { get; set; }

        [Column("last_login")]
        public string LastLogin { get; set; }
    }

    [Table("characters")]
    public class CharacterRow
    {
        [PrimaryKey][Column("character_id")]
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

        [Column("current_map"), Indexed]
        public string CurrentMap { get; set; } = "World_01";

        [Column("free_points")]
        public int FreePoints { get; set; } = 0;

        [Column("alloc_str")]  public int AllocSTR { get; set; } = 0;
        [Column("alloc_agi")]  public int AllocAGI { get; set; } = 0;
        [Column("alloc_vit")]  public int AllocVIT { get; set; } = 0;
        [Column("alloc_dex")]  public int AllocDEX { get; set; } = 0;
        [Column("alloc_int")]  public int AllocINT { get; set; } = 0;
        [Column("alloc_luk")]  public int AllocLUK { get; set; } = 0;

        [Column("base_str")]   public int BaseSTR { get; set; } = 10;
        [Column("base_agi")]   public int BaseAGI { get; set; } = 10;
        [Column("base_vit")]   public int BaseVIT { get; set; } = 10;
        [Column("base_dex")]   public int BaseDEX { get; set; } = 10;
        [Column("base_int")]   public int BaseINT { get; set; } = 10;
        [Column("base_luk")]   public int BaseLUK { get; set; } = 10;
    }

    [Table("inventory")]
    public class InventoryRow
    {
        [PrimaryKey, AutoIncrement][Column("id")]
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

    [Table("gem_loadout")]
    public class GemLoadoutRow
    {
        [PrimaryKey][Column("character_id")]
        public string CharacterId { get; set; }

        [Column("slot_q")] public string SlotQ { get; set; } = "";
        [Column("slot_w")] public string SlotW { get; set; } = "";
        [Column("slot_e")] public string SlotE { get; set; } = "";
        [Column("slot_r")] public string SlotR { get; set; } = "";
    }

    [Table("economy_log")]
    public class EconomyLogRow
    {
        [PrimaryKey, AutoIncrement][Column("id")]
        public int Id { get; set; }

        [Column("character_id"), NotNull, Indexed]
        public string CharacterId { get; set; }

        [Column("event_type"), NotNull]
        public string EventType { get; set; }

        [Column("value")]
        public float Value { get; set; }

        [Column("timestamp"), NotNull]
        public string Timestamp { get; set; }
    }

#else
    // ── Stubs das tabelas para o cliente compilar sem SQLite ──────────────
    public class AccountRow  { public string Username { get; set; } public string PasswordHash { get; set; } public string CreatedAt { get; set; } public string LastLogin { get; set; } }
    public class CharacterRow { public string CharacterId { get; set; } public string Username { get; set; } public string CharacterName { get; set; } public int Race { get; set; } public int Level { get; set; } = 1; public long Experience { get; set; } = 0; public long ExpToNext { get; set; } = 100; public float CurrentHP { get; set; } = 100f; public float CurrentMP { get; set; } = 50f; public float PosX { get; set; } = 0f; public float PosY { get; set; } = 1f; public float PosZ { get; set; } = 0f; public string CurrentMap { get; set; } = "World_01"; public int FreePoints { get; set; } = 0; public int AllocSTR { get; set; } = 0; public int AllocAGI { get; set; } = 0; public int AllocVIT { get; set; } = 0; public int AllocDEX { get; set; } = 0; public int AllocINT { get; set; } = 0; public int AllocLUK { get; set; } = 0; public int BaseSTR { get; set; } = 10; public int BaseAGI { get; set; } = 10; public int BaseVIT { get; set; } = 10; public int BaseDEX { get; set; } = 10; public int BaseINT { get; set; } = 10; public int BaseLUK { get; set; } = 10; }
    public class InventoryRow { public int Id { get; set; } public string CharacterId { get; set; } public string ItemId { get; set; } public int Quantity { get; set; } = 1; public int SlotIndex { get; set; } = -1; public bool IsEquipped { get; set; } = false; }
    public class GemLoadoutRow { public string CharacterId { get; set; } public string SlotQ { get; set; } = ""; public string SlotW { get; set; } = ""; public string SlotE { get; set; } = ""; public string SlotR { get; set; } = ""; }
    public class EconomyLogRow { public int Id { get; set; } public string CharacterId { get; set; } public string EventType { get; set; } public float Value { get; set; } public string Timestamp { get; set; } }
#endif

    // ══════════════════════════════════════════════════════════════════════
    // DatabaseManager v8
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DatabaseManager v8
    ///
    /// CORREÇÃO CRÍTICA v8 — Login impossível no Editor:
    ///
    ///   CAUSA RAIZ:
    ///     O bloco #if UNITY_SERVER || UNITY_EDITOR fazia o Editor inicializar
    ///     o banco SQLite localmente usando o salt de desenvolvimento hardcoded
    ///     ("DEV_ONLY_SALT_TROQUE_ANTES_DO_LAUNCH"). Contas criadas pelo Editor
    ///     tinham hash gerado com esse salt.
    ///
    ///     O servidor .exe usa RPG_SERVER_SALT da variável de ambiente (salt real).
    ///     Como os dois salts são diferentes, o hash não batia e o login falhava
    ///     com "senha incorreta" mesmo a senha estando correta.
    ///
    ///   SOLUÇÃO:
    ///     Removido UNITY_EDITOR de todos os #if — banco SQLite agora compila
    ///     e inicializa APENAS em UNITY_SERVER (build dedicada).
    ///     O Editor passa a usar os stubs no-op, igual ao cliente normal.
    ///     Toda operação de conta/personagem passa pelo servidor via rede,
    ///     que é o comportamento correto para um MMO.
    ///
    ///   IMPACTO:
    ///     - No Editor: DatabaseManager.Instance existe mas todos os métodos
    ///       retornam null/false/lista vazia (stubs). O banco nunca é aberto.
    ///     - No servidor .exe: comportamento idêntico ao v7.
    ///     - Testes no Editor: use o servidor .exe rodando localmente (localhost).
    ///
    ///   AÇÃO NECESSÁRIA APÓS ESTA CORREÇÃO:
    ///     Delete o banco antigo criado pelo Editor antes de testar:
    ///     %AppData%\..\LocalLow\DefaultCompany\rpgonline\rpg_server.db
    ///     Crie novas contas pelo jogo conectando ao servidor .exe.
    ///
    ///   Todas as correções v7 mantidas (WAL, write thread, challenge-response).
    /// </summary>
    public class DatabaseManager : MonoBehaviour
    {
        public static DatabaseManager Instance { get; private set; }

#if UNITY_SERVER
        private SQLiteConnection                 _db;
        private readonly object                  _dbLock             = new object();
        private bool                             _closed             = false;
        private readonly ConcurrentQueue<Action> _writeQueue         = new ConcurrentQueue<Action>();
        private Thread                           _writeThread;
        private volatile bool                    _writeThreadRunning;
        private readonly ManualResetEventSlim    _writeEvent         = new ManualResetEventSlim(false);
#endif

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

#if UNITY_SERVER
            InitializeDatabase();
            StartWriteThread();
#else
            Debug.Log("[DatabaseManager] Modo cliente/editor — banco desabilitado. " +
                      "Operações de conta passam pelo servidor via rede (correto).");
#endif
        }

        private void OnDestroy()         => FlushAndClose();
        private void OnApplicationQuit() => FlushAndClose();

        private void FlushAndClose()
        {
#if UNITY_SERVER
            if (_closed) return;
            _closed             = true;
            _writeThreadRunning = false;
            _writeEvent.Set();
            _writeThread?.Join(3000);
            lock (_dbLock) { _db?.Close(); _db = null; }
#endif
        }

        // ══════════════════════════════════════════════════════════════════
        // IMPLEMENTAÇÃO — apenas no servidor dedicado
        // ══════════════════════════════════════════════════════════════════

#if UNITY_SERVER

        // ── Inicialização ──────────────────────────────────────────────────

        private void InitializeDatabase()
        {
            try
            {
                string dbPath = System.IO.Path.Combine(Application.persistentDataPath, "rpg_server.db");
                Debug.Log($"[DatabaseManager] Banco em: {dbPath}");

                _db = new SQLiteConnection(dbPath);
                _db.ExecuteScalar<string>("PRAGMA journal_mode=WAL;");
                _db.Execute("PRAGMA foreign_keys = ON;");
                _db.Execute("PRAGMA synchronous = NORMAL;");

                _db.CreateTable<AccountRow>();
                _db.CreateTable<CharacterRow>();
                _db.CreateTable<InventoryRow>();
                _db.CreateTable<GemLoadoutRow>();
                _db.CreateTable<EconomyLogRow>();

                Debug.Log("[DatabaseManager] Banco inicializado com sucesso.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[DatabaseManager] ERRO CRÍTICO ao inicializar banco: {e}");
            }
        }

        // ── Thread de escrita assíncrona ───────────────────────────────────

        private void StartWriteThread()
        {
            _writeThreadRunning = true;
            _writeThread = new Thread(WriteThreadLoop)
            {
                Name         = "DB_WriteThread",
                IsBackground = true
            };
            _writeThread.Start();
        }

        private void WriteThreadLoop()
        {
            while (_writeThreadRunning)
            {
                _writeEvent.Wait(500);
                _writeEvent.Reset();
                while (_writeQueue.TryDequeue(out Action action))
                {
                    try   { action(); }
                    catch (Exception e) { Debug.LogError($"[DB] Write thread erro: {e.Message}"); }
                }
            }
            // Flush do que sobrou ao encerrar
            while (_writeQueue.TryDequeue(out Action action))
            {
                try { action(); } catch { }
            }
        }

        private void EnqueueWrite(Action writeAction)
        {
            _writeQueue.Enqueue(writeAction);
            _writeEvent.Set();
        }

        // ══════════════════════════════════════════════════════════════════
        // CONTAS
        // ══════════════════════════════════════════════════════════════════

        public bool AccountExists(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            try
            {
                lock (_dbLock)
                    return _db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM accounts WHERE LOWER(username) = LOWER(?)",
                        username.Trim()) > 0;
            }
            catch (Exception e) { Debug.LogError($"[DB] AccountExists: {e.Message}"); return false; }
        }

        public string TryCreateAccount(string username, string clientPasswordHash)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Trim().Length < 4)
                return "Username deve ter ao menos 4 caracteres.";
            if (string.IsNullOrWhiteSpace(clientPasswordHash))
                return "Senha inválida.";
            if (AccountExists(username))
                return "Username já está em uso.";
            try
            {
                string storedHash = GameManager.ServerHashForStorage(clientPasswordHash);
                lock (_dbLock)
                {
                    _db.Insert(new AccountRow
                    {
                        Username     = username.Trim().ToLower(),
                        PasswordHash = storedHash,
                        CreatedAt    = DateTime.UtcNow.ToString("o"),
                        LastLogin    = null
                    });
                }
                Debug.Log($"[DB] Conta criada: {username}");
                return null;
            }
            catch (Exception e) { Debug.LogError($"[DB] TryCreateAccount: {e.Message}"); return "Erro interno."; }
        }

        public AccountData TryLoginWithSignedHash(string username, string clientSignedHash, string sessionNonce)
        {
            if (string.IsNullOrWhiteSpace(username)        ||
                string.IsNullOrWhiteSpace(clientSignedHash) ||
                string.IsNullOrWhiteSpace(sessionNonce))
                return null;

            try
            {
                AccountRow row;
                lock (_dbLock)
                {
                    row = _db.FindWithQuery<AccountRow>(
                        "SELECT * FROM accounts WHERE LOWER(username) = LOWER(?)",
                        username.Trim());
                }

                if (row == null)
                {
                    // Delay anti timing-attack (dificulta enumerar usuários pelo tempo de resposta)
                    System.Threading.Thread.Sleep(50);
                    return null;
                }

                bool valid = GameManager.ValidateLoginWithNonce(
                    row.PasswordHash,
                    clientSignedHash,
                    sessionNonce);

                if (!valid)
                {
                    Debug.LogWarning($"[DB] TryLoginWithSignedHash: senha incorreta para '{username}'.");
                    return null;
                }

                UpdateLastLogin(row.Username);

                return new AccountData
                {
                    Username     = row.Username,
                    PasswordHash = row.PasswordHash,
                    Characters   = LoadCharacters(row.Username)
                };
            }
            catch (Exception e)
            {
                Debug.LogError($"[DB] TryLoginWithSignedHash: {e.Message}");
                return null;
            }
        }

        /// <summary>Login legado sem nonce — mantido para compatibilidade interna.</summary>
        public AccountData TryLoginWithHash(string username, string clientPasswordHash)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(clientPasswordHash))
                return null;
            try
            {
                string storedHash = GameManager.ServerHashForStorage(clientPasswordHash);
                AccountRow row;
                lock (_dbLock)
                {
                    row = _db.FindWithQuery<AccountRow>(
                        "SELECT * FROM accounts WHERE LOWER(username) = LOWER(?) AND password_hash = ?",
                        username.Trim(), storedHash);
                }
                if (row == null) return null;

                UpdateLastLogin(row.Username);

                return new AccountData
                {
                    Username     = row.Username,
                    PasswordHash = row.PasswordHash,
                    Characters   = LoadCharacters(row.Username)
                };
            }
            catch (Exception e) { Debug.LogError($"[DB] TryLoginWithHash: {e.Message}"); return null; }
        }

        private void UpdateLastLogin(string username)
        {
            string now   = DateTime.UtcNow.ToString("o");
            string uname = username;
            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                        _db.Execute(
                            "UPDATE accounts SET last_login = ? WHERE username = ?",
                            now, uname);
                }
                catch (Exception e) { Debug.LogError($"[DB] UpdateLastLogin: {e.Message}"); }
            });
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
                List<CharacterRow> rows;
                lock (_dbLock)
                    rows = _db.Query<CharacterRow>(
                        "SELECT * FROM characters WHERE LOWER(username) = LOWER(?) ORDER BY level DESC",
                        username.Trim());
                foreach (var row in rows) list.Add(RowToCharacterData(row));
            }
            catch (Exception e) { Debug.LogError($"[DB] LoadCharacters: {e.Message}"); }
            return list;
        }

        public CharacterData LoadCharacter(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return null;
            try
            {
                CharacterRow row;
                lock (_dbLock) { row = _db.Find<CharacterRow>(characterId); }
                return row != null ? RowToCharacterData(row) : null;
            }
            catch (Exception e) { Debug.LogError($"[DB] LoadCharacter: {e.Message}"); return null; }
        }

        public CharacterData LoadCharacterForAccount(string characterId, string username)
        {
            if (string.IsNullOrWhiteSpace(characterId) || string.IsNullOrWhiteSpace(username)) return null;
            try
            {
                CharacterRow row;
                lock (_dbLock)
                    row = _db.FindWithQuery<CharacterRow>(
                        "SELECT * FROM characters WHERE character_id = ? AND LOWER(username) = LOWER(?)",
                        characterId, username.Trim());
                return row != null ? RowToCharacterData(row) : null;
            }
            catch (Exception e) { Debug.LogError($"[DB] LoadCharacterForAccount: {e.Message}"); return null; }
        }

        public List<CharacterData> GetCharactersInMap(string mapName)
        {
            var list = new List<CharacterData>();
            if (string.IsNullOrWhiteSpace(mapName)) return list;
            try
            {
                List<CharacterRow> rows;
                lock (_dbLock)
                    rows = _db.Query<CharacterRow>(
                        "SELECT * FROM characters WHERE current_map = ?", mapName);
                foreach (var row in rows) list.Add(RowToCharacterData(row));
            }
            catch (Exception e) { Debug.LogError($"[DB] GetCharactersInMap: {e.Message}"); }
            return list;
        }

        public string TryCreateCharacter(string username, string name, CharacterRace race)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
                return "Nome deve ter ao menos 2 caracteres.";
            try
            {
                lock (_dbLock)
                {
                    int count = _db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM characters WHERE LOWER(username) = LOWER(?)",
                        username.Trim());
                    if (count >= 5) return "Limite de 5 personagens por conta atingido.";

                    int nameCount = _db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM characters WHERE LOWER(character_name) = LOWER(?)",
                        name.Trim());
                    if (nameCount > 0) return "Já existe um personagem com esse nome.";

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
                    var stats = ch.GetDerivedStats();
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
                        PosX = 0f, PosY = 1f, PosZ = 0f,
                        CurrentMap    = ch.CurrentMap,
                        FreePoints    = 0,
                        AllocSTR = 0, AllocAGI = 0, AllocVIT = 0,
                        AllocDEX = 0, AllocINT = 0, AllocLUK = 0,
                        BaseSTR = 10, BaseAGI = 10, BaseVIT = 10,
                        BaseDEX = 10, BaseINT = 10, BaseLUK = 10
                    });
                }
                Debug.Log($"[DB] Personagem criado: {name} ({race}) para {username}");
                return null;
            }
            catch (Exception e) { Debug.LogError($"[DB] TryCreateCharacter: {e.Message}"); return "Erro interno."; }
        }

        public void SaveCharacter(CharacterData ch, string username)
        {
            if (ch == null || string.IsNullOrWhiteSpace(ch.CharacterId)) return;

            string charId  = ch.CharacterId;
            string uname   = username.Trim();
            int    level   = ch.Level;
            long   exp     = ch.Experience;
            long   expNext = ch.ExperienceToNextLevel;
            float  hp      = ch.CurrentHP;
            float  mp      = ch.CurrentMP;
            float  px = ch.PosX, py = ch.PosY, pz = ch.PosZ;
            string map     = ch.CurrentMap ?? "World_01";
            int    fp      = ch.FreeAttributePoints;
            int    aSTR    = ch.AllocatedSTR;
            int    aAGI    = ch.AllocatedAGI;
            int    aVIT    = ch.AllocatedVIT;
            int    aDEX    = ch.AllocatedDEX;
            int    aINT    = ch.AllocatedINT;
            int    aLUK    = ch.AllocatedLUK;

            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                    {
                        _db.Execute(@"
                            UPDATE characters SET
                                level       = ?, experience  = ?, exp_to_next = ?,
                                current_hp  = ?, current_mp  = ?,
                                pos_x       = ?, pos_y       = ?, pos_z       = ?,
                                current_map = ?, free_points = ?,
                                alloc_str   = ?, alloc_agi   = ?, alloc_vit   = ?,
                                alloc_dex   = ?, alloc_int   = ?, alloc_luk   = ?
                            WHERE character_id = ? AND LOWER(username) = LOWER(?)",
                            level, exp, expNext, hp, mp, px, py, pz, map, fp,
                            aSTR, aAGI, aVIT, aDEX, aINT, aLUK, charId, uname);
                    }
                }
                catch (Exception e) { Debug.LogError($"[DB] SaveCharacter: {e.Message}"); }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // INVENTÁRIO
        // ══════════════════════════════════════════════════════════════════

        public List<InventoryRow> LoadInventory(string characterId)
        {
            try
            {
                lock (_dbLock)
                    return _db.Query<InventoryRow>(
                        "SELECT * FROM inventory WHERE character_id = ?", characterId);
            }
            catch (Exception e) { Debug.LogError($"[DB] LoadInventory: {e.Message}"); return new List<InventoryRow>(); }
        }

        public void SaveInventory(string characterId, string username, List<RPG.Data.InventorySlotData> slots)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return;

            string charId = characterId;
            var copy = new List<RPG.Data.InventorySlotData>(slots);

            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                    {
                        _db.Execute("DELETE FROM inventory WHERE character_id = ?", charId);
                        foreach (var slot in copy)
                        {
                            if (string.IsNullOrEmpty(slot.ItemId)) continue;
                            _db.Insert(new InventoryRow
                            {
                                CharacterId = charId,
                                ItemId      = slot.ItemId,
                                Quantity    = slot.Quantity,
                                SlotIndex   = slot.SlotIndex,
                                IsEquipped  = false
                            });
                        }
                    }
                }
                catch (Exception e) { Debug.LogError($"[DB] SaveInventory: {e.Message}"); }
            });
        }

        public void AddItem(string characterId, string itemId, int quantity = 1, int slot = -1)
        {
            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                        _db.Insert(new InventoryRow
                        {
                            CharacterId = characterId,
                            ItemId      = itemId,
                            Quantity    = quantity,
                            SlotIndex   = slot,
                            IsEquipped  = false
                        });
                }
                catch (Exception e) { Debug.LogError($"[DB] AddItem: {e.Message}"); }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // GEM LOADOUT
        // ══════════════════════════════════════════════════════════════════

        public PowerGemLoadout LoadGemLoadout(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return new PowerGemLoadout();
            try
            {
                GemLoadoutRow row;
                lock (_dbLock)
                    row = _db.Find<GemLoadoutRow>(characterId);

                if (row == null) return new PowerGemLoadout();

                return new PowerGemLoadout
                {
                    SlotQ = row.SlotQ ?? "",
                    SlotW = row.SlotW ?? "",
                    SlotE = row.SlotE ?? "",
                    SlotR = row.SlotR ?? ""
                };
            }
            catch (Exception e) { Debug.LogError($"[DB] LoadGemLoadout: {e.Message}"); return new PowerGemLoadout(); }
        }

        public void SaveGemLoadout(string characterId, PowerGemLoadout loadout)
        {
            if (string.IsNullOrWhiteSpace(characterId)) return;

            string charId = characterId;
            string q = loadout.SlotQ ?? "";
            string w = loadout.SlotW ?? "";
            string e = loadout.SlotE ?? "";
            string r = loadout.SlotR ?? "";

            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                    {
                        _db.InsertOrReplace(new GemLoadoutRow
                        {
                            CharacterId = charId,
                            SlotQ = q, SlotW = w, SlotE = e, SlotR = r
                        });
                    }
                }
                catch (Exception ex) { Debug.LogError($"[DB] SaveGemLoadout: {ex.Message}"); }
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // LOG DE ECONOMIA
        // ══════════════════════════════════════════════════════════════════

        public void LogEconomy(string characterId, string eventType, float value)
        {
            string ts = DateTime.UtcNow.ToString("o");
            EnqueueWrite(() =>
            {
                try
                {
                    lock (_dbLock)
                        _db.Insert(new EconomyLogRow
                        {
                            CharacterId = characterId,
                            EventType   = eventType,
                            Value       = value,
                            Timestamp   = ts
                        });
                }
                catch (Exception e) { Debug.LogError($"[DB] LogEconomy: {e.Message}"); }
            });
        }

        // ── Helpers ────────────────────────────────────────────────────────

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
                BaseAttributes = new BaseAttributes
                {
                    STR = row.BaseSTR, AGI = row.BaseAGI, VIT = row.BaseVIT,
                    DEX = row.BaseDEX, INT = row.BaseINT, LUK = row.BaseLUK
                },
                EquipmentBonuses = new EquipmentBonuses()
            };
        }

#else
        // ══════════════════════════════════════════════════════════════════
        // STUBS CLIENTE E EDITOR — no-op completo, banco nunca é acessado
        // ══════════════════════════════════════════════════════════════════

        public bool                AccountExists(string u)                                                      => false;
        public string              TryCreateAccount(string u, string h)                                         => null;
        public AccountData         TryLoginWithHash(string u, string h)                                         => null;
        public AccountData         TryLoginWithSignedHash(string u, string sh, string n)                        => null;
        public List<CharacterData> LoadCharacters(string u)                                                      => new List<CharacterData>();
        public CharacterData       LoadCharacter(string id)                                                      => null;
        public CharacterData       LoadCharacterForAccount(string id, string u)                                  => null;
        public List<CharacterData> GetCharactersInMap(string m)                                                 => new List<CharacterData>();
        public string              TryCreateCharacter(string u, string n, CharacterRace r)                      => null;
        public void                SaveCharacter(CharacterData ch, string u)                                    { }
        public List<InventoryRow>  LoadInventory(string id)                                                     => new List<InventoryRow>();
        public void                SaveInventory(string cid, string u, List<RPG.Data.InventorySlotData> slots)  { }
        public void                AddItem(string id, string item, int qty = 1, int slot = -1)                 { }
        public PowerGemLoadout     LoadGemLoadout(string id)                                                    => new PowerGemLoadout();
        public void                SaveGemLoadout(string id, PowerGemLoadout l)                                { }
        public void                LogEconomy(string id, string ev, float v)                                   { }
#endif
    }
}
