using UnityEngine;
using UnityEngine.AI;
using RPG.Data;
using System;
using System.Collections.Generic;

namespace RPG.Character
{
    /// <summary>
    /// PlayerEntity — representação VISUAL/LOCAL do personagem no cliente.
    ///
    /// REGRAS ABSOLUTAS:
    ///   - Este script NÃO toma decisões de jogo.
    ///   - HP, MP, XP, Level, Stats: todos chegam do servidor via NetworkPlayer SyncVars.
    ///   - Os únicos métodos que alteram estado são os Set* chamados pelo NetworkPlayer.
    ///   - Não há regen, dano, heal, save ou lógica de combate aqui.
    ///   - NavMeshAgent é movido SOMENTE por CmdMoveTo confirmado pelo servidor.
    ///
    /// CORREÇÕES v2:
    ///   - Camera cacheada no Awake para evitar Camera.main por frame.
    ///   - IsInitialized verificado em todos os métodos Set*.
    ///   - OnDisable garante limpeza do HashSet mesmo em crashes.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class PlayerEntity : MonoBehaviour
    {
        // ── Registro estático (usado pelo NetworkMonsterEntity para encontrar players) ──
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

        // ── Cache da câmera (evita Camera.main toda frame) ─────────────────
        private Camera _cachedCamera;
        public  Camera MainCamera => _cachedCamera != null ? _cachedCamera : (_cachedCamera = Camera.main);

        // ── Alvo selecionado (só visual/local — servidor não usa isso) ─────
        public ITargetable CurrentTarget { get; private set; }

        // ── Lifecycle ──────────────────────────────────────────────────────
        private void OnEnable()  => All.Add(this);
        private void OnDisable() => All.Remove(this);

        private void Awake()
        {
            _agent        = GetComponent<NavMeshAgent>();
            _cachedCamera = Camera.main;
        }

        // ── Inicialização (chamada pelo NetworkPlayer via RpcInitializeLocalPlayer) ──

        /// <summary>
        /// Inicializa o PlayerEntity com dados confirmados pelo servidor.
        /// Chamado UMA VEZ após o servidor validar e registrar o personagem.
        /// </summary>
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

        public void SetHPFromServer(float hp, float maxHp)
        {
            if (!IsInitialized) return;

            bool wasDead = IsDead;

            if (Stats != null) Stats.MaxHP = maxHp;
            CurrentHP = Mathf.Clamp(hp, 0f, maxHp);

            OnHPChanged?.Invoke(CurrentHP, maxHp);

            bool nowDead = IsDead;
            if (nowDead != wasDead)
            {
                if (nowDead) _agent?.ResetPath();
                OnDeathChanged?.Invoke(nowDead);
            }
        }

        public void SetMPFromServer(float mp, float maxMp)
        {
            if (!IsInitialized) return;
            if (Stats != null) Stats.MaxMP = maxMp;
            CurrentMP = Mathf.Clamp(mp, 0f, maxMp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
        }

        public void RefreshStatsFromServer(float maxHp, float maxMp)
        {
            if (!IsInitialized) return;
            if (Stats != null)
            {
                Stats.MaxHP = maxHp;
                Stats.MaxMP = maxMp;
            }
            CurrentHP = Mathf.Min(CurrentHP, maxHp);
            CurrentMP = Mathf.Min(CurrentMP, maxMp);
            OnStatsChanged?.Invoke();
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);
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

            if (Stats != null) { Stats.MaxHP = maxHp; Stats.MaxMP = maxMp; }
            CurrentHP = hp;
            CurrentMP = mp;

            OnDeathChanged?.Invoke(false);
            OnHPChanged?.Invoke(CurrentHP, maxHp);
            OnMPChanged?.Invoke(CurrentMP, maxMp);

            Debug.Log($"[PlayerEntity] Respawn em {position}");
        }

        // ── Movimento ──────────────────────────────────────────────────────

        public void MoveToConfirmed(Vector3 destination)
        {
            if (IsDead || _agent == null || !_agent.isOnNavMesh) return;
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