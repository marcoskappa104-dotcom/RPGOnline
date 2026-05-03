using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Data;
using RPG.UI;
using RPG.Character;
using RPG.Combat;
using System.Collections;
using System.Collections.Generic;

namespace RPG.Network
{
    public enum MonsterDisposition { Passive, Neutral, Aggressive }

    /// <summary>
    /// NetworkMonsterEntity v10 — SERVIDOR É AUTORIDADE TOTAL.
    ///
    /// MUDANÇAS DESTA VERSÃO:
    ///   - CmdRequestAttack renomeado para CmdRequestSkill (consistência com SkillSystem).
    ///   - CmdRequestSkill agora valida MP e cooldown do atacante no servidor.
    ///   - Nenhum valor de dano ou stats vem do cliente.
    ///   - Servidor busca stats do atacante via NetworkPlayer.ServerStats.
    ///   - Servidor chama RpcSkillConfirmed/RpcSkillRejected no NetworkPlayer atacante.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    [RequireComponent(typeof(NetworkTransformReliable))]
    public class NetworkMonsterEntity : NetworkBehaviour, ITargetable
    {
        // ── Config — Identidade ────────────────────────────────────────────
        [Header("Identidade")]
        [SerializeField] private string monsterDisplayName = "Monstro";
        [SerializeField] private int    level              = 1;

        // ── Config — Comportamento ─────────────────────────────────────────
        [Header("Comportamento")]
        [SerializeField] private MonsterDisposition disposition = MonsterDisposition.Aggressive;

        // ── Config — Atributos ─────────────────────────────────────────────
        [Header("Atributos Base")]
        [SerializeField] private int baseSTR = 12;
        [SerializeField] private int baseAGI = 8;
        [SerializeField] private int baseVIT = 10;
        [SerializeField] private int baseDEX = 8;
        [SerializeField] private int baseINT = 5;
        [SerializeField] private int baseLUK = 5;

        // ── Config — Ranges ────────────────────────────────────────────────
        [Header("Ranges de IA")]
        [SerializeField] private float aggroRange     = 10f;
        [SerializeField] private float attackRange    = 2.5f;
        [SerializeField] private float kiteDistance   = 1.8f;
        [SerializeField] private float attackCooldown = 2f;
        [SerializeField] private float leashRange     = 30f;
        [SerializeField] private float pathUpdateRate = 0.2f;

        [Header("Performance de IA")]
        [SerializeField] private float aggroScanInterval = 0.5f;

        // ── Config — Patrulha ──────────────────────────────────────────────
        [Header("Patrulha")]
        [SerializeField] private bool        usePatrolPoints = false;
        [SerializeField] private Transform[] patrolPoints;
        [SerializeField] private float       patrolWaitTime  = 2f;
        [SerializeField] private float       patrolRadius    = 12f;

        // ── Config — Fuga ──────────────────────────────────────────────────
        [Header("Fuga (apenas Passive)")]
        [SerializeField] private float fleeDuration  = 6f;
        [SerializeField] private float fleeSpeedMult = 1.5f;

        // ── Config — Morte e Respawn ───────────────────────────────────────
        [Header("Morte e Respawn")]
        [SerializeField] private float hideDelay    = 3f;
        [SerializeField] private float respawnDelay = 15f;

        [Header("Recompensa")]
        [SerializeField] private long expReward = 50;

        // ── Visuals ────────────────────────────────────────────────────────
        [Header("Visuals")]
        [SerializeField] private GameObject         selectionIndicator;
        [SerializeField] private MonsterHealthBarUI healthBarUI;
        [SerializeField] private GameObject         visualRoot;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnCurrentHPChanged))]
        private float _currentHP;

        [SyncVar]
        private float _maxHP;

        [SyncVar(hook = nameof(OnDeadChanged))]
        private bool _isDead;

        // ── ITargetable ────────────────────────────────────────────────────
        public string  DisplayName => monsterDisplayName;
        public float   CurrentHP   => _currentHP;
        public float   MaxHP       => _maxHP;
        public bool    IsDead      => _isDead;
        public Vector3 Position    => transform.position;

        public void OnSelected()   { if (selectionIndicator) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator) selectionIndicator.SetActive(false); }

        /// <summary>
        /// TakeDamage via ITargetable — somente para uso em modo offline/teste.
        /// Em multiplayer, o dano sempre passa por CmdRequestSkill.
        /// </summary>
        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (!isServer || _isDead) return;
            bool  crit = StatsCalculator.RollCrit(_stats?.CRIT ?? 0f);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(rawAtk, _stats?.DEF ?? 0f, crit, _stats?.CritDMG ?? 1.5f)
                : StatsCalculator.CalculateMagicDamage(rawMatk, _stats?.MDEF ?? 0f, crit, _stats?.CritDMG ?? 1.5f);
            dmg = Mathf.Max(1f, dmg);
            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            RpcShowDamage(dmg, crit, transform.position);
            if (_currentHP <= 0f) ServerDie();
        }

        // ── Stats e estado interno ─────────────────────────────────────────
        private DerivedStats _stats;
        private Dictionary<uint, float> _damageLog = new Dictionary<uint, float>();

        // ── IA ─────────────────────────────────────────────────────────────
        private enum State { Idle, Patrol, Chase, Combat, Flee, ReturnHome, Dead }

        private State         _state = State.Idle;
        private NavMeshAgent  _agent;
        private Animator      _animator;
        private NetworkPlayer _aggroTarget;

        private float _attackTimer;
        private float _pathTimer;
        private float _patrolWaitTimer;
        private float _fleeTimer;
        private float _regenTimer;
        private float _aggroScanTimer;

        private int     _patrolIndex;
        private bool    _waitingAtPatrolPoint;
        private bool    _wasAttacked;
        private Vector3 _currentPatrolTarget;
        private bool    _patrolTargetSet;

        private Vector3 _homePosition;
        private float   _patrolRadiusRuntime;

        private const float REGEN_INTERVAL = 3f;
        private const float REGEN_PERCENT  = 0.05f;

        // ── Tolerância de range (compensa lag de rede) ─────────────────────
        private const float SKILL_RANGE_TOLERANCE = 2.0f;

        // ── Init ───────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent    = GetComponent<NavMeshAgent>();
            _animator = GetComponentInChildren<Animator>();

            var attrs = new BaseAttributes
            {
                STR = baseSTR, AGI = baseAGI, VIT = baseVIT,
                DEX = baseDEX, INT = baseINT, LUK = baseLUK
            };
            _stats = StatsCalculator.Calculate(attrs, level, CharacterRace.Human);
        }

        public override void OnStartServer()
        {
            _homePosition        = transform.position;
            _patrolRadiusRuntime = patrolRadius;
            ServerReset();
        }

        public override void OnStartClient()
        {
            if (selectionIndicator) selectionIndicator.SetActive(false);
            healthBarUI?.UpdateBar(_currentHP, _maxHP);
        }

        [Server]
        public void SetSpawnData(Vector3 homePosition, float newPatrolRadius)
        {
            _homePosition        = homePosition;
            _patrolRadiusRuntime = newPatrolRadius;
            transform.position   = homePosition;
            _patrolTargetSet     = false;
        }

        [Server]
        private void ServerReset()
        {
            _maxHP          = _stats.MaxHP;
            _currentHP      = _maxHP;
            _isDead         = false;
            _wasAttacked    = false;
            _state          = State.Idle;
            _aggroTarget    = null;
            _attackTimer    = 0f;
            _pathTimer      = 0f;
            _fleeTimer      = 0f;
            _regenTimer     = 0f;
            _aggroScanTimer = 0f;
            _patrolIndex    = 0;
            _waitingAtPatrolPoint = false;
            _patrolTargetSet      = false;
            _damageLog.Clear();

            transform.position = _homePosition;

            if (_agent != null)
            {
                _agent.enabled = true;
                _agent.speed   = _stats.ASPD;
                if (_agent.isOnNavMesh)
                    _agent.Warp(_homePosition);
            }

            RpcOnRespawned();
        }

        // ── Update ─────────────────────────────────────────────────────────

        private void Update()
        {
            if (!isServer || _isDead) return;

            _pathTimer      += Time.deltaTime;
            _attackTimer    += Time.deltaTime;
            _aggroScanTimer += Time.deltaTime;

            switch (_state)
            {
                case State.Idle:       ServerIdle();       break;
                case State.Patrol:     ServerPatrol();     break;
                case State.Chase:      ServerChase();      break;
                case State.Combat:     ServerCombat();     break;
                case State.Flee:       ServerFlee();       break;
                case State.ReturnHome: ServerReturnHome(); break;
            }
        }

        // ── Estados de IA ──────────────────────────────────────────────────

        private void ServerIdle()
        {
            if (TryAggro()) return;
            bool hasArea   = _patrolRadiusRuntime > 0.5f && !usePatrolPoints;
            bool hasPoints = usePatrolPoints && patrolPoints != null && patrolPoints.Length > 0;
            if (hasArea || hasPoints) _state = State.Patrol;
        }

        private void ServerPatrol()
        {
            if (TryAggro()) return;
            if (usePatrolPoints) PatrolWithWaypoints();
            else                 PatrolInArea();
        }

        private void PatrolWithWaypoints()
        {
            if (!_agent.isOnNavMesh) return;
            if (_waitingAtPatrolPoint)
            {
                _patrolWaitTimer += Time.deltaTime;
                if (_patrolWaitTimer < patrolWaitTime) return;
                _waitingAtPatrolPoint = false;
            }
            if (_pathTimer >= pathUpdateRate)
            {
                _pathTimer = 0f;
                if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
                {
                    _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                    _agent.SetDestination(patrolPoints[_patrolIndex].position);
                    _waitingAtPatrolPoint = true;
                    _patrolWaitTimer      = 0f;
                }
            }
        }

        private void PatrolInArea()
        {
            if (!_agent.isOnNavMesh) return;
            bool arrived = !_agent.pathPending && _agent.remainingDistance < 0.6f;

            if (_waitingAtPatrolPoint)
            {
                _patrolWaitTimer += Time.deltaTime;
                if (_patrolWaitTimer >= patrolWaitTime)
                {
                    _waitingAtPatrolPoint = false;
                    _patrolTargetSet      = false;
                }
                return;
            }

            if (!_patrolTargetSet || arrived)
            {
                if (arrived && _patrolTargetSet)
                {
                    _waitingAtPatrolPoint = true;
                    _patrolWaitTimer      = 0f;
                    return;
                }
                if (TryGetRandomAreaPoint(_homePosition, _patrolRadiusRuntime, out Vector3 dest))
                {
                    _agent.SetDestination(dest);
                    _currentPatrolTarget = dest;
                    _patrolTargetSet     = true;
                }
            }
        }

        private void ServerChase()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            float distFromHome = Vector3.Distance(transform.position, _homePosition);
            if (distFromHome > leashRange) { ResetAggro(); EnterReturnHome(); return; }
            float distToTarget = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (distToTarget > aggroRange * 2.5f) { ResetAggro(); return; }
            if (distToTarget <= attackRange)
            {
                _attackTimer = 0f;
                _state       = State.Combat;
                _agent.ResetPath();
                return;
            }
            if (_pathTimer >= pathUpdateRate && _agent.isOnNavMesh)
            {
                _pathTimer              = 0f;
                _agent.stoppingDistance = attackRange * 0.85f;
                _agent.SetDestination(_aggroTarget.transform.position);
            }
        }

        private void ServerCombat()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            float distFromHome = Vector3.Distance(transform.position, _homePosition);
            if (distFromHome > leashRange) { ResetAggro(); EnterReturnHome(); return; }
            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (dist > attackRange * 1.4f) { _state = State.Chase; return; }

            if (_agent.isOnNavMesh)
            {
                if (dist < kiteDistance)
                {
                    Vector3 away = (transform.position - _aggroTarget.transform.position).normalized;
                    _agent.SetDestination(transform.position + away * (kiteDistance + 0.5f));
                }
                else _agent.ResetPath();
            }

            Vector3 dir = _aggroTarget.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, Quaternion.LookRotation(dir), 10f * Time.deltaTime);

            if (_attackTimer >= attackCooldown)
            {
                _attackTimer = 0f;
                ServerAttack();
            }
        }

        private void ServerFlee()
        {
            _fleeTimer += Time.deltaTime;
            if (_fleeTimer >= fleeDuration || !_agent.isOnNavMesh)
            {
                _agent.speed = _stats.ASPD;
                EnterReturnHome();
                return;
            }
            if (_pathTimer >= pathUpdateRate * 2f && _aggroTarget != null)
            {
                _pathTimer = 0f;
                Vector3 fleeDir = (transform.position - _aggroTarget.transform.position).normalized;
                Vector3 fleePos = transform.position + fleeDir * (aggroRange * 1.5f);
                if (NavMesh.SamplePosition(fleePos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                    _agent.SetDestination(hit.position);
            }
        }

        private void ServerReturnHome()
        {
            _regenTimer += Time.deltaTime;
            if (_regenTimer >= REGEN_INTERVAL)
            {
                _regenTimer = 0f;
                _currentHP  = Mathf.Min(_maxHP, _currentHP + _maxHP * REGEN_PERCENT);
            }
            if (!_agent.isOnNavMesh) return;
            if (_pathTimer >= pathUpdateRate)
            {
                _pathTimer              = 0f;
                _agent.stoppingDistance = 0.5f;
                _agent.SetDestination(_homePosition);
            }
            if (Vector3.Distance(transform.position, _homePosition) < 1.5f)
            {
                _agent.ResetPath();
                _wasAttacked     = false;
                _patrolTargetSet = false;
                _damageLog.Clear();
                _state = State.Idle;
            }
        }

        private void EnterReturnHome()
        {
            _state          = State.ReturnHome;
            _aggroTarget    = null;
            _regenTimer     = 0f;
            _agent.stoppingDistance = 0.5f;
        }

        // ── Aggro ──────────────────────────────────────────────────────────

        private bool TryAggro()
        {
            if (disposition == MonsterDisposition.Passive) return false;
            if (disposition == MonsterDisposition.Neutral && !_wasAttacked) return false;
            if (_aggroScanTimer < aggroScanInterval) return false;
            _aggroScanTimer = 0f;

            float         closest = aggroRange;
            NetworkPlayer found   = null;

            foreach (var np in NetworkPlayer.All)
            {
                if (np == null || np.Dead) continue;
                float dist = Vector3.Distance(transform.position, np.transform.position);
                if (dist < closest) { closest = dist; found = np; }
            }

            if (found != null)
            {
                _aggroTarget = found;
                _state       = State.Chase;
                _pathTimer   = pathUpdateRate;
                _attackTimer = 0f;
                return true;
            }
            return false;
        }

        private void ResetAggro()
        {
            _aggroTarget = null;
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.ResetPath();
                _agent.stoppingDistance = 0.3f;
            }
            _attackTimer     = 0f;
            _patrolTargetSet = false;

            float distToHome = Vector3.Distance(transform.position, _homePosition);
            if (distToHome > leashRange * 0.5f)
                EnterReturnHome();
            else
                _state = State.Idle;
        }

        // ── Ataque do monstro → jogador ────────────────────────────────────

        [Server]
        private void ServerAttack()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) return;

            float playerFlee = _aggroTarget.ServerStats?.FLEE ?? 20f;
            bool  hit        = StatsCalculator.RollHit(_stats.HIT, playerFlee);

            if (!hit) { RpcShowMiss(_aggroTarget.transform.position); return; }

            bool  crit      = StatsCalculator.RollCrit(_stats.CRIT);
            float playerDEF = _aggroTarget.ServerStats?.DEF ?? 10f;
            float dmg       = StatsCalculator.CalculatePhysicalDamage(
                                  _stats.ATK, playerDEF, crit, _stats.CritDMG);

            _aggroTarget.ServerApplyDamage(dmg);
            RpcPlayAnim("Attack");
        }

        // ── CmdRequestSkill — cliente solicita atacar este monstro ─────────

        /// <summary>
        /// Recebe APENAS (netId do atacante, índice da skill, tipo físico/mágico).
        /// ZERO dados de dano ou stats vêm do cliente.
        /// Servidor valida: existência, vida, MP, cooldown, range, e aplica dano.
        /// </summary>
        [Command(requiresAuthority = false)]
        public void CmdRequestSkill(uint attackerNetId, int skillIndex, bool isPhysical)
        {
            // Valida monstro
            if (_isDead) return;

            // Encontra o atacante
            NetworkPlayer attacker = null;
            foreach (var np in NetworkPlayer.All)
            {
                if (np != null && np.netId == attackerNetId) { attacker = np; break; }
            }
            if (attacker == null || attacker.Dead) return;

            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            // Busca dados da skill no SkillSystem do atacante
            var skillSystem = attacker.GetComponent<SkillSystem>();
            var skill = skillSystem?.GetSkill(skillIndex);
            if (skill == null)
            {
                attacker.RpcSkillRejected(attacker.connectionToClient, skillIndex, "Skill inválida.");
                return;
            }

            // ── Validação de cooldown no servidor ──────────────────────────
            // O servidor mantém cooldowns independentemente do cliente
            // (cooldowns ficam no NetworkPlayer._serverCooldowns)
            // Nota: a validação de cooldown já ocorreu em NetworkPlayer.CmdRequestSelfSkill
            // Para skills de dano no monstro, o cooldown é verificado via NetworkPlayer
            // indireto — melhor prática seria mover cooldowns para NetworkPlayer inteiramente.
            // Por ora, o monstro não checa cooldown (o NetworkPlayer já fez isso),
            // mas verifica range no servidor para anti-cheat.

            // ── Validação de range (anti-cheat) ───────────────────────────
            float dist = Vector3.Distance(attacker.transform.position, transform.position);
            if (dist > skill.Range + SKILL_RANGE_TOLERANCE)
            {
                // Range inválido — pode ser lag ou trapaça
                // Não rejeita imediatamente (pode ser lag), mas loga para análise
                Debug.LogWarning($"[Server] {attacker.CharacterName} skill range suspeito: " +
                                 $"dist={dist:0.1} range={skill.Range} tolerance={SKILL_RANGE_TOLERANCE}");
                // Para anti-cheat rigoroso: descomentar a linha abaixo
                // attacker.RpcSkillRejected(attacker.connectionToClient, skillIndex, "Fora de alcance."); return;
            }

            // ── Validação de MP ────────────────────────────────────────────
            if (attacker.CurrentMP < skill.ManaCost)
            {
                attacker.RpcSkillRejected(attacker.connectionToClient, skillIndex, "MP insuficiente!");
                return;
            }

            // Consome MP no servidor (via NetworkPlayer)
            attacker.ServerConsumeMP(skill.ManaCost);

            // ── Cálculo de dano (servidor faz tudo) ────────────────────────
            ServerTakeDamageFromPlayer(attacker, atkStats, skillIndex, isPhysical, skill);

            // ── Confirma para o cliente (inicia cooldown visual) ───────────
            attacker.RpcSkillConfirmed(attacker.connectionToClient, skillIndex, skill.Cooldown);
        }

        [Server]
        private void ServerTakeDamageFromPlayer(
            NetworkPlayer attacker, DerivedStats atkStats,
            int skillIndex, bool isPhysical, SkillData skill)
        {
            bool hit = StatsCalculator.RollHit(atkStats.HIT, _stats.FLEE);
            if (!hit) { RpcShowMiss(transform.position); return; }

            bool  crit = StatsCalculator.RollCrit(atkStats.CRIT);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(
                      atkStats.ATK * skill.AtkMultiplier, _stats.DEF, crit, atkStats.CritDMG)
                : StatsCalculator.CalculateMagicDamage(
                      atkStats.MATK * skill.AtkMultiplier, _stats.MDEF, crit, atkStats.CritDMG);

            dmg = Mathf.Max(1f, dmg);

            // Registra dano para distribuição proporcional de XP
            if (!_damageLog.ContainsKey(attacker.netId))
                _damageLog[attacker.netId] = 0f;
            _damageLog[attacker.netId] += dmg;

            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            RpcShowDamage(dmg, crit, transform.position);

            // Reação conforme disposição
            switch (disposition)
            {
                case MonsterDisposition.Passive:
                    if (_state != State.Flee && _state != State.ReturnHome && _state != State.Dead)
                    {
                        _aggroTarget = attacker;
                        _fleeTimer   = 0f;
                        _state       = State.Flee;
                        _agent.speed = _stats.ASPD * fleeSpeedMult;
                        _pathTimer   = pathUpdateRate;
                    }
                    break;

                case MonsterDisposition.Neutral:
                    _wasAttacked = true;
                    if (_state == State.Idle || _state == State.Patrol || _state == State.ReturnHome)
                    {
                        _aggroTarget = attacker;
                        _state       = State.Chase;
                        _pathTimer   = pathUpdateRate;
                        _attackTimer = 0f;
                    }
                    break;

                case MonsterDisposition.Aggressive:
                    if (_state == State.Idle || _state == State.Patrol)
                    {
                        _aggroTarget = attacker;
                        _state       = State.Chase;
                        _pathTimer   = pathUpdateRate;
                        _attackTimer = 0f;
                    }
                    break;
            }

            if (_currentHP <= 0f) ServerDie();
        }

        // ── Morte e Respawn ────────────────────────────────────────────────

        [Server]
        private void ServerDie()
        {
            if (_isDead) return;
            _isDead = true;
            _state  = State.Dead;

            if (_agent != null)
            {
                if (_agent.isOnNavMesh) _agent.ResetPath();
                _agent.enabled = false;
            }

            Debug.Log($"[NetworkMonster] {monsterDisplayName} morreu!");
            ServerDistributeExp();
            StartCoroutine(ServerDeathSequence());
        }

        [Server]
        private void ServerDistributeExp()
        {
            if (_damageLog.Count == 0) return;

            float totalDamage = 0f;
            foreach (var kv in _damageLog) totalDamage += kv.Value;
            if (totalDamage <= 0f) return;

            foreach (var kv in _damageLog)
            {
                float proportion    = kv.Value / totalDamage;
                long  xpShare       = (long)Mathf.Max(1f, expReward * proportion);
                uint  attackerNetId = kv.Key;

                foreach (var np in NetworkPlayer.All)
                {
                    if (np != null && np.netId == attackerNetId)
                    {
                        np.ServerGrantExp(xpShare);
                        break;
                    }
                }
            }
            _damageLog.Clear();
        }

        [Server]
        private IEnumerator ServerDeathSequence()
        {
            RpcOnDied(transform.position);
            if (hideDelay > 0f) yield return new WaitForSeconds(hideDelay);
            RpcHideVisuals();
            if (respawnDelay <= 0f) yield break;
            yield return new WaitForSeconds(respawnDelay);
            if (isServer) ServerReset();
        }

        // ── NavMesh Helper ─────────────────────────────────────────────────

        private bool TryGetRandomAreaPoint(Vector3 center, float radius, out Vector3 result)
        {
            for (int i = 0; i < 15; i++)
            {
                Vector2 rand2D    = Random.insideUnitCircle * radius;
                Vector3 candidate = center + new Vector3(rand2D.x, 0f, rand2D.y);
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                {
                    result = hit.position;
                    return true;
                }
            }
            result = center;
            return false;
        }

        // ── ClientRpcs ─────────────────────────────────────────────────────

        [ClientRpc]
        private void RpcShowDamage(float dmg, bool crit, Vector3 pos)
        {
            Color c = crit ? Color.yellow : Color.white;
            FloatingTextManager.Instance?.Show(
                crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}", pos + Vector3.up, c);
        }

        [ClientRpc]
        private void RpcShowMiss(Vector3 pos)
            => FloatingTextManager.Instance?.Show("MISS", pos + Vector3.up * 0.5f, Color.gray);

        [ClientRpc]
        private void RpcPlayAnim(string trigger)
            => _animator?.SetTrigger(trigger);

        [ClientRpc]
        private void RpcOnDied(Vector3 pos)
        {
            OnDeselected();
            UIManager.Instance?.ClearTargetPanel();
            FloatingTextManager.Instance?.Show("Morto!", pos + Vector3.up, Color.red);
        }

        [ClientRpc]
        private void RpcHideVisuals()
        {
            if (visualRoot         != null) visualRoot.SetActive(false);
            if (selectionIndicator != null) selectionIndicator.SetActive(false);
            if (healthBarUI        != null) healthBarUI.gameObject.SetActive(false);
        }

        [ClientRpc]
        private void RpcOnRespawned()
        {
            if (visualRoot != null) visualRoot.SetActive(true);
            if (selectionIndicator != null) selectionIndicator.SetActive(false);
            if (healthBarUI != null)
            {
                healthBarUI.gameObject.SetActive(true);
                healthBarUI.UpdateBar(_currentHP, _maxHP);
            }
        }

        // ── SyncVar Hooks ──────────────────────────────────────────────────

        private void OnCurrentHPChanged(float _, float newVal)
            => healthBarUI?.UpdateBar(newVal, _maxHP);

        private void OnDeadChanged(bool _, bool nowDead)
        {
            if (nowDead && _agent != null) _agent.enabled = false;
        }

        // ── Gizmos ─────────────────────────────────────────────────────────

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Color aggroColor = disposition switch
            {
                MonsterDisposition.Passive    => Color.green,
                MonsterDisposition.Neutral    => Color.yellow,
                MonsterDisposition.Aggressive => Color.red,
                _ => Color.red
            };
            Gizmos.color = aggroColor;
            Gizmos.DrawWireSphere(transform.position, aggroRange);
            Gizmos.color = new Color(1f, 0.3f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, attackRange);
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, kiteDistance);
            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, leashRange);
            if (Application.isPlaying)
            {
                Gizmos.color = new Color(1f, 0.8f, 0f, 0.5f);
                Gizmos.DrawWireSphere(_homePosition, _patrolRadiusRuntime);
                Gizmos.color = Color.cyan;
                Gizmos.DrawSphere(_homePosition, 0.3f);
            }
        }
#endif
    }
}
