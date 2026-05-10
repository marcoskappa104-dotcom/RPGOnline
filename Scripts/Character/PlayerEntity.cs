using UnityEngine;
using UnityEngine.AI;
using RPG.Data;
using System;
using System.Collections.Generic;

namespace RPG.Character
{
    /// <summary>
    /// PlayerEntity v4
    ///
    /// CORREÇÕES v4:
    ///
    ///   BUG — Mutação direta de Stats.MaxHP/MaxMP nos hooks de SyncVar:
    ///     SetHPFromServer, SetMPFromServer e RefreshStatsFromServer modificavam
    ///     Stats.MaxHP/MaxMP diretamente em um objeto DerivedStats compartilhado.
    ///     Como DerivedStats é uma classe (referência), qualquer outro sistema
    ///     lendo Stats simultaneamente via _player.Stats.MaxHP recebia o valor
    ///     intermediário durante a mutação.
    ///     SOLUÇÃO: SetHPFromServer/SetMPFromServer NÃO modificam mais Stats diretamente.
    ///     RefreshStatsFromServer cria um clone de Stats e aplica os novos valores,
    ///     tornando a troca atômica (substituição de referência).
    ///
    ///   BUG — MoveToConfirmed não verificava NavMesh:
    ///     Chamado com destino inválido causava warning do NavMeshAgent.
    ///     SOLUÇÃO: verifica isOnNavMesh antes de SetDestination.
    ///
    ///   MELHORIA — MainCamera property simplificada para campo privado com
    ///     lazy init segura (não recalcula toda chamada).
    ///
    ///   Todas as correções v3 mantidas (FullRefreshStatsFromData, IsInitialized, etc).
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class PlayerEntity : MonoBehaviour
    {
        // ── Registro estático ──────────────────────────────────────────────
        public static readonly HashSet<PlayerEntity> All = new HashSet<PlayerEntity>();

        // ── Dados recebidos do servidor ────────────────────────────────────
        public CharacterData Data  { get; private set; }
        public DerivedStats  Stats { get; private set; }

        public float CurrentHP { get; private set; }
        public float CurrentMP { get; private set; }

        public bool IsInitialized => Data != null && Stats != null;
        public bool IsDead        => CurrentHP <= 0f;

        // ── Eventos para a UI ──────────────────────────────────────────────
        public event Action<float, float> OnHPChanged;
        public event Action<float, float> OnMPChanged;
        public event Action<bool>         OnDeathChanged;
        public event Action               OnStatsChanged;
        public event Action               OnInitialized;

        // ── Componentes ────────────────────────────────────────────────────
        private NavMeshAgent _agent;
        public  NavMeshAgent Agent => _agent;

        // ── Cache da câmera ────────────────────────────────────────────────
        private Camera _cachedCamera;
        public  Camera MainCamera
        {
            get
            {
                if (_cachedCamera == null)
                    _cachedCamera = Camera.main;
                return _cachedCamera;
            }
        }

        // ── Alvo selecionado ───────────────────────────────────────────────
        public ITargetable CurrentTarget { get; private set; }

        // ── Lifecycle ──────────────────────────────────────────────────────
        private void OnEnable()  => All.Add(this);
        private void OnDisable() => All.Remove(this);

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _cachedCamera = Camera.main;
        }

        // ── Inicialização ──────────────────────────────────────────────────

        public void InitializeFromServer(CharacterData data)
        {
            if (data == null)
            {
                Debug.LogError("[PlayerEntity] InitializeFromServer: data é null.");
                return;
            }

            Data  = data;
            Stats = data.GetDerivedStats();

            CurrentHP = Mathf.Clamp(data.CurrentHP, 0f, Stats.MaxHP);
            CurrentMP = Mathf.Clamp(data.CurrentMP, 0f, Stats.MaxMP);

            ConfigureAgent();

            Debug.Log($"[PlayerEntity] Inicializado: {data.CharacterName} " +
                      $"Lv{data.Level} HP:{CurrentHP:0}/{Stats.MaxHP:0}");

            OnInitialized?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        // ── Atualizações de estado vindas do servidor ─────────────────────

        /// <summary>
        /// CORREÇÃO v4: NÃO modifica Stats.MaxHP diretamente.
        /// Usa o MaxHP atual de Stats como referência para o clamp.
        /// Se MaxHP precisar mudar, use RefreshStatsFromServer.
        /// </summary>
        public void SetHPFromServer(float hp, float maxHp)
        {
            if (!IsInitialized) return;

            bool wasDead = IsDead;

            // Atualiza MaxHP de forma atômica via clone se necessário
            if (Stats.MaxHP != maxHp)
            {
                var updated = Stats.Clone();
                updated.MaxHP = maxHp;
                Stats = updated;
            }

            CurrentHP = Mathf.Clamp(hp, 0f, maxHp);

            OnHPChanged?.Invoke(CurrentHP, maxHp);

            bool nowDead = IsDead;
            if (nowDead != wasDead)
            {
                if (nowDead) _agent?.ResetPath();
                OnDeathChanged?.Invoke(nowDead);
            }
        }

        /// <summary>
        /// CORREÇÃO v4: NÃO modifica Stats.MaxMP diretamente.
        /// </summary>
        public void SetMPFromServer(float mp, float maxMp)
        {
            if (!IsInitialized) return;

            if (Stats.MaxMP != maxMp)
            {
                var updated = Stats.Clone();
                updated.MaxMP = maxMp;
                Stats = updated;
            }

            CurrentMP = Mathf.Clamp(mp, 0f, maxMp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        /// <summary>
        /// CORREÇÃO v4: substituição atômica de Stats via Clone.
        /// Outros leitores de Stats nunca veem estado intermediário.
        /// </summary>
        public void RefreshStatsFromServer(float maxHp, float maxMp)
        {
            if (!IsInitialized) return;

            var updated = Stats.Clone();
            updated.MaxHP = maxHp;
            updated.MaxMP = maxMp;
            Stats = updated;  // troca atômica de referência

            CurrentHP = Mathf.Min(CurrentHP, maxHp);
            CurrentMP = Mathf.Min(CurrentMP, maxMp);

            OnStatsChanged?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        /// <summary>
        /// Recalcula TODOS os DerivedStats a partir dos dados atuais de Data.
        /// Chamado quando o servidor confirma alocação de atributo (StatsVersion bump).
        /// </summary>
        public void FullRefreshStatsFromData()
        {
            if (!IsInitialized || Data == null) return;

            Stats = Data.GetDerivedStats();  // novo objeto, substituição atômica

            ConfigureAgent();

            if (CurrentHP > Stats.MaxHP) CurrentHP = Stats.MaxHP;
            if (CurrentMP > Stats.MaxMP) CurrentMP = Stats.MaxMP;

            OnStatsChanged?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, Stats.MaxHP);
            OnMPChanged?.Invoke(CurrentMP, Stats.MaxMP);
        }

        public void UpdateDataFromServer(int level, long exp, long expToNext,
                                         int freePoints,
                                         int allocSTR, int allocAGI, int allocVIT,
                                         int allocDEX, int allocINT, int allocLUK)
        {
            if (Data == null) return;
            Data.Level                 = level;
            Data.Experience            = exp;
            Data.ExperienceToNextLevel = expToNext;
            Data.FreeAttributePoints   = freePoints;
            Data.AllocatedSTR          = allocSTR;
            Data.AllocatedAGI          = allocAGI;
            Data.AllocatedVIT          = allocVIT;
            Data.AllocatedDEX          = allocDEX;
            Data.AllocatedINT          = allocINT;
            Data.AllocatedLUK          = allocLUK;
        }

        // ── Morte e Respawn ────────────────────────────────────────────────

        public void OnServerDeath()
        {
            CurrentHP = 0f;
            _agent?.ResetPath();
            OnHPChanged?.Invoke(0f, Stats?.MaxHP ?? 1f);
            OnDeathChanged?.Invoke(true);
            Debug.Log($"[PlayerEntity] Morte confirmada: {Data?.CharacterName}");
        }

        public void OnServerRespawn(Vector3 position, float hp, float maxHp, float mp, float maxMp)
        {
            if (!IsInitialized) return;

            transform.position = position;
            _agent?.Warp(position);

            // Substituição atômica de Stats
            var updated = Stats.Clone();
            updated.MaxHP = maxHp;
            updated.MaxMP = maxMp;
            Stats = updated;

            CurrentHP = hp;
            CurrentMP = mp;

            OnDeathChanged?.Invoke(false);
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);

            Debug.Log($"[PlayerEntity] Respawn em {position}");
        }

        // ── Movimento ──────────────────────────────────────────────────────

        /// <summary>
        /// CORREÇÃO v4: verifica isOnNavMesh antes de SetDestination.
        /// </summary>
        public void MoveToConfirmed(Vector3 destination)
        {
            if (IsDead || _agent == null || !_agent.isOnNavMesh) return;

            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(destination);
        }

        public void StopMovement() => _agent?.ResetPath();

        public bool HasReachedDestination()
        {
            if (_agent == null) return true;
            return !_agent.pathPending
                && _agent.remainingDistance <= _agent.stoppingDistance
                && (!_agent.hasPath || _agent.velocity.sqrMagnitude < 0.01f);
        }

        // ── Alvo ──────────────────────────────────────────────────────────

        public void SetTarget(ITargetable target)
        {
            CurrentTarget?.OnDeselected();
            CurrentTarget = target;
            CurrentTarget?.OnSelected();
        }

        public void ClearTarget()
        {
            CurrentTarget?.OnDeselected();
            CurrentTarget = null;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void ConfigureAgent()
        {
            if (_agent == null || Stats == null) return;
            _agent.speed            = Mathf.Clamp(Stats.MoveSpeed, 2f, 10f);
            _agent.stoppingDistance = 0.5f;
        }
    }
}