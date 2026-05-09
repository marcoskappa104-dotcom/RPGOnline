using UnityEngine;
using System.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.Threading;
using RPG.Data;

// O using SQLite só compila no servidor/editor para não linkar a DLL nativa no cliente
#if UNITY_SERVER || UNITY_EDITOR
using SQLite;
#endif

namespace RPG.Managers
{
    // ── Tabelas SQLite ─────────────────────────────────────────────────────
    // As classes existem em todos os builds (para evitar erros de referência),
    // mas os atributos SQLite só são compilados no servidor/editor.

#if UNITY_SERVER || UNITY_EDITOR
    [Table("accounts")]
#endif
    public class AccountRow
    {
#if UNITY_SERVER || UNITY_EDITOR
        [PrimaryKey][Column("username")]
#endif
        public string Username { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("password_hash"), NotNull]
#endif
        public string PasswordHash { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("created_at"), NotNull]
#endif
        public string CreatedAt { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("last_login")]
#endif
        public string LastLogin { get; set; }
    }

#if UNITY_SERVER || UNITY_EDITOR
    [Table("characters")]
#endif
    public class CharacterRow
    {
#if UNITY_SERVER || UNITY_EDITOR
        [PrimaryKey][Column("character_id")]
#endif
        public string CharacterId { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("username"), NotNull, Indexed]
#endif
        public string Username { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("character_name"), NotNull, Unique]
#endif
        public string CharacterName { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("race")]
#endif
        public int Race { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("level")]
#endif
        public int Level { get; set; } = 1;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("experience")]
#endif
        public long Experience { get; set; } = 0;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("exp_to_next")]
#endif
        public long ExpToNext { get; set; } = 100;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("current_hp")]
#endif
        public float CurrentHP { get; set; } = 100f;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("current_mp")]
#endif
        public float CurrentMP { get; set; } = 50f;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("pos_x")]
#endif
        public float PosX { get; set; } = 0f;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("pos_y")]
#endif
        public float PosY { get; set; } = 1f;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("pos_z")]
#endif
        public float PosZ { get; set; } = 0f;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("current_map"), Indexed]
#endif
        public string CurrentMap { get; set; } = "World_01";

#if UNITY_SERVER || UNITY_EDITOR
        [Column("free_points")]
#endif
        public int FreePoints { get; set; } = 0;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("alloc_str")]
#endif
        public int AllocSTR { get; set; } = 0;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("alloc_agi")]
#endif
        public int AllocAGI { get; set; } = 0;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("alloc_vit")]
#endif
        public int AllocVIT { get; set; } = 0;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("alloc_dex")]
#endif
        public int AllocDEX { get; set; } = 0;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("alloc_int")]
#endif
        public int AllocINT { get; set; } = 0;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("alloc_luk")]
#endif
        public int AllocLUK { get; set; } = 0;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("base_str")]
#endif
        public int BaseSTR { get; set; } = 10;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("base_agi")]
#endif
        public int BaseAGI { get; set; } = 10;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("base_vit")]
#endif
        public int BaseVIT { get; set; } = 10;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("base_dex")]
#endif
        public int BaseDEX { get; set; } = 10;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("base_int")]
#endif
        public int BaseINT { get; set; } = 10;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("base_luk")]
#endif
        public int BaseLUK { get; set; } = 10;
    }

#if UNITY_SERVER || UNITY_EDITOR
    [Table("inventory")]
#endif
    public class InventoryRow
    {
#if UNITY_SERVER || UNITY_EDITOR
        [PrimaryKey, AutoIncrement][Column("id")]
#endif
        public int Id { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("character_id"), NotNull, Indexed]
#endif
        public string CharacterId { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("item_id"), NotNull]
#endif
        public string ItemId { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("quantity")]
#endif
        public int Quantity { get; set; } = 1;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("slot_index")]
#endif
        public int SlotIndex { get; set; } = -1;

#if UNITY_SERVER || UNITY_EDITOR
        [Column("is_equipped")]
#endif
        public bool IsEquipped { get; set; } = false;
    }

#if UNITY_SERVER || UNITY_EDITOR
    [Table("gem_loadout")]
#endif
    public class GemLoadoutRow
    {
#if UNITY_SERVER || UNITY_EDITOR
        [PrimaryKey][Column("character_id")]
#endif
        public string CharacterId { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("slot_q")]
#endif
        public string SlotQ { get; set; } = "";

#if UNITY_SERVER || UNITY_EDITOR
        [Column("slot_w")]
#endif
        public string SlotW { get; set; } = "";

#if UNITY_SERVER || UNITY_EDITOR
        [Column("slot_e")]
#endif
        public string SlotE { get; set; } = "";

#if UNITY_SERVER || UNITY_EDITOR
        [Column("slot_r")]
#endif
        public string SlotR { get; set; } = "";
    }

#if UNITY_SERVER || UNITY_EDITOR
    [Table("economy_log")]
#endif
    public class EconomyLogRow
    {
#if UNITY_SERVER || UNITY_EDITOR
        [PrimaryKey, AutoIncrement][Column("id")]
#endif
        public int Id { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("character_id"), NotNull, Indexed]
#endif
        public string CharacterId { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("event_type"), NotNull]
#endif
        public string EventType { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("value")]
#endif
        public float Value { get; set; }

#if UNITY_SERVER || UNITY_EDITOR
        [Column("timestamp"), NotNull]
#endif
        public string Timestamp { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════
    // DatabaseManager v6
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DatabaseManager v6
    ///
    /// CORREÇÃO CRÍTICA v6 — DllNotFoundException: sqlite3 no cliente:
    ///
    ///   CAUSA: O cliente (build sem flag -server) tentava inicializar o
    ///   SQLiteConnection e chamava sqlite3.dll via P/Invoke. Como o cliente
    ///   não tem (e não deve ter) essa DLL nativa, o erro ocorria.
    ///
    ///   SOLUÇÃO: Toda a lógica de banco é compilada SOMENTE com
    ///   #if UNITY_SERVER || UNITY_EDITOR. No cliente, o Awake não inicializa
    ///   nada e todos os métodos são stubs que retornam valores nulos/vazios.
    ///
    ///   FLUXO CORRETO:
    ///   - Build Cliente: DatabaseManager existe mas é completamente no-op.
    ///                    sqlite3.dll não é carregada. Zero erros.
    ///   - Build Servidor: banco inicializa normalmente via InitializeDatabase().
    ///   - Editor (Play Mode): banco ativo — usado para testes com Host/Server.
    ///
    ///   SQLITE DLL NO SERVIDOR WINDOWS:
    ///   Coloque sqlite3.dll em Assets/Plugins/x86_64/sqlite3.dll
    ///   Configure no Inspector do plugin:
    ///     Platform: Standalone  |  CPU: x86_64  |  OS: Windows
    ///     ☑ Include in build  |  ☐ Load on startup
    /// </summary>
    public class DatabaseManager : MonoBehaviour
    {
        public static DatabaseManager Instance { get; private set; }

#if UNITY_SERVER || UNITY_EDITOR
        private SQLiteConnection               _db;
        private readonly object                _dbLock             = new object();
        private bool                           _closed             = false;
        private readonly ConcurrentQueue<Action> _writeQueue       = new ConcurrentQueue<Action>();
        private Thread                         _writeThread;
        private volatile bool                  _writeThreadRunning;
        private readonly ManualResetEventSlim  _writeEvent         = new ManualResetEventSlim(false);
#endif

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

#if UNITY_SERVER || UNITY_EDITOR
            InitializeDatabase();
            StartWriteThread();
#else
            // Cliente: DatabaseManager instanciado como no-op. Sem banco, sem DLL.
            Debug.Log("[DatabaseManager] Modo cliente — banco desabilitado (correto).");
#endif
        }

        private void OnDestroy()         => FlushAndClose();
        private void OnApplicationQuit() => FlushAndClose();

        private void FlushAndClose()
        {
#if UNITY_SERVER || UNITY_EDITOR
            if (_closed) return;
            _closed = true;
            _writeThreadRunning = false;
            _writeEvent.Set();
            _writeThread?.Join(3000);
            lock (_dbLock) { _db?.Close(); _db = null; }
#endif
        }

        // ══════════════════════════════════════════════════════════════════
        // IMPLEMENTAÇÃO SERVIDOR / EDITOR
        // ══════════════════════════════════════════════════════════════════

#if UNITY_SERVER || UNITY_EDITOR

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
                Debug.LogError($"[DatabaseManager] ERRO ao inicializar: {e}");
            }
        }

        // ── Thread de escrita assíncrona ───────────────────────────────────

        private void StartWriteThread()
        {
            _writeThreadRunning = true;
            _writeThread = new Thread(WriteThreadLoop)
            {
                Name = "DB_WriteThread",
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
                    catch (Exception e) { Debug.LogError($"[DB] Write thread: {e.Message}"); }
                }
            }
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

                string uname = row.Username;
                string now   = DateTime.UtcNow.ToString("o");
                EnqueueWrite(() =>
                {
                    lock (_dbLock)
                        _db.Execute("UPDATE accounts SET last_login = ? WHERE username = ?", now, uname);
                });

                return new AccountData
                {
                    Username     = row.Username,
                    PasswordHash = row.PasswordHash,
                    Characters   = LoadCharacters(row.Username)
                };
            }
            catch (Exception e) { Debug.LogError($"[DB] TryLoginWithHash: {e.Message}"); return null; }
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
                        "SELECT COUNT(*) FROM characters WHERE LOWER(username) = LOWER(?)", username.Trim());
                    if (count >= 5) return "Limite de 5 personagens por conta atingido.";

                    int nameCount = _db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM characters WHERE LOWER(character_name) = LOWER(?)", name.Trim());
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
        // STUBS CLIENTE — no-op completo, zero acesso à DLL nativa
        // ══════════════════════════════════════════════════════════════════

        public bool AccountExists(string username)                                                          => false;
        public string TryCreateAccount(string username, string clientPasswordHash)                         => null;
        public AccountData TryLoginWithHash(string username, string clientPasswordHash)                    => null;
        public List<CharacterData> LoadCharacters(string username)                                         => new List<CharacterData>();
        public CharacterData LoadCharacter(string characterId)                                             => null;
        public CharacterData LoadCharacterForAccount(string characterId, string username)                  => null;
        public List<CharacterData> GetCharactersInMap(string mapName)                                     => new List<CharacterData>();
        public string TryCreateCharacter(string username, string name, CharacterRace race)                => null;
        public void SaveCharacter(CharacterData ch, string username)                                       { }
        public List<InventoryRow> LoadInventory(string characterId)                                        => new List<InventoryRow>();
        public void SaveInventory(string cid, string u, List<RPG.Data.InventorySlotData> slots)           { }
        public void AddItem(string characterId, string itemId, int quantity = 1, int slot = -1)            { }
        public PowerGemLoadout LoadGemLoadout(string characterId)                                          => new PowerGemLoadout();
        public void SaveGemLoadout(string characterId, PowerGemLoadout loadout)                            { }
        public void LogEconomy(string characterId, string eventType, float value)                          { }
#endif
    }
}