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
    /// NetworkMonsterEntity v14
    ///
    /// CORREÇÕES v14:
    ///
    ///   1. ORDEM DE SPAWN CORRIGIDA (causa raiz da patrulha quebrada):
    ///      Em versões anteriores, OnStartServer() chamava ServerReset() com
    ///      _homePosition = Vector3.zero porque SetSpawnData() ainda não havia rodado.
    ///      O monstro era teleportado para a origem (0,0,0) e patrulhava lá.
    ///      SOLUÇÃO: OnStartServer() NÃO chama ServerReset(). SetSpawnData() define
    ///      _homePosition e então chama ServerReset(). Para monstros colocados
    ///      manualmente na cena (sem SetSpawnData), o primeiro Update() do servidor
    ///      detecta o flag e chama ServerReset() com transform.position como home.
    ///
    ///   2. PATRULHA GARANTIDA: estado inicial é Patrol. PathUpdateLoop tem um
    ///      primeiro tick imediato (yield return null) para não depender de esperar
    ///      pathUpdateRate antes de dar o primeiro SetDestination.
    ///
    ///   3. RESPAWN CORRETO: ServerDeathSequence reseta _serverResetDone antes de
    ///      chamar ServerReset() para garantir que o reset funciona na segunda morte.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkMonsterEntity : NetworkBehaviour, ITargetable
    {
        [Header("Identidade")]
        [SerializeField] private string monsterDisplayName = "Monstro";
        [SerializeField] private int    level              = 1;

        [Header("Comportamento")]
        [SerializeField] private MonsterDisposition disposition = MonsterDisposition.Aggressive;

        [Header("Atributos Base")]
        [SerializeField] private int baseSTR = 12;
        [SerializeField] private int baseAGI = 8;
        [SerializeField] private int baseVIT = 10;
        [SerializeField] private int baseDEX = 8;
        [SerializeField] private int baseINT = 5;
        [SerializeField] private int baseLUK = 5;

        [Header("Ranges de IA")]
        [SerializeField] private float aggroRange      = 10f;
        [SerializeField] private float attackRange     = 2.5f;
        [SerializeField] private float kiteDistance    = 1.8f;
        [SerializeField] private float attackCooldown  = 2f;
        [SerializeField] private float leashRange      = 30f;

        [Header("Performance de IA")]
        [SerializeField] private float aggroScanInterval = 0.4f;
        [SerializeField] private float pathUpdateRate    = 0.05f;

        [Header("Patrulha")]
        [SerializeField] private bool        usePatrolPoints = false;
        [SerializeField] private Transform[] patrolPoints;
        [SerializeField] private float       patrolWaitTime  = 2f;
        [SerializeField] private float       patrolRadius    = 12f;

        [Header("Fuga (apenas Passive)")]
        [SerializeField] private float fleeDuration  = 6f;
        [SerializeField] private float fleeSpeedMult = 1.5f;

        [Header("Morte e Respawn")]
        [SerializeField] private float hideDelay    = 3f;
        [SerializeField] private float respawnDelay = 15f;

        [Header("Recompensa")]
        [SerializeField] private long expReward = 50;

        [Header("Visuals")]
        [SerializeField] private GameObject         selectionIndicator;
        [SerializeField] private MonsterHealthBarUI healthBarUI;
        [SerializeField] private GameObject         visualRoot;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnCurrentHPChanged))] private float _currentHP;
        [SyncVar]                                     private float _maxHP;
        [SyncVar(hook = nameof(OnDeadChanged))]       private bool  _isDead;

        // ── ITargetable ────────────────────────────────────────────────────
        public string  DisplayName => monsterDisplayName;
        public float   CurrentHP   => _currentHP;
        public float   MaxHP       => _maxHP;
        public bool    IsDead      => _isDead;
        public Vector3 Position    => transform.position;

        public void OnSelected()   { if (selectionIndicator) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator) selectionIndicator.SetActive(false); }

        public void TakeDamage(float rawAtk, float rawMatk, bool isPhysical)
        {
            if (!isServer || _isDead) return;
            bool  crit = StatsCalculator.RollCrit(_stats?.CRIT ?? 0f);
            float dmg  = isPhysical
                ? StatsCalculator.CalculatePhysicalDamage(rawAtk, _stats?.DEF ?? 0f, crit, _stats?.CritDMG ?? 1.5f)
                : StatsCalculator.CalculateMagicDamage(rawMatk, _stats?.MDEF ?? 0f, crit, _stats?.CritDMG ?? 1.5f);
            ApplyDamageInternal(Mathf.Max(1f, dmg));
        }

        // ── Estado interno ─────────────────────────────────────────────────
        private DerivedStats _stats;
        private readonly Dictionary<uint, float> _damageLog = new();

        private enum AIState { Idle, Patrol, Chase, Combat, Flee, ReturnHome, Dead }
        private AIState       _state = AIState.Idle;
        private NavMeshAgent  _agent;
        private Animator      _animator;
        private NetworkPlayer _aggroTarget;
        private bool          _wasAttacked;

        private float _attackTimer;
        private float _fleeTimer;

        private int     _patrolIndex;
        private bool    _patrolWaiting;
        private Vector3 _currentPatrolTarget;
        private bool    _patrolTargetSet;

        private Vector3 _homePosition;
        private float   _patrolRadiusRuntime;

        // CORREÇÃO: flags de controle de ordem de spawn
        private bool _serverResetDone = false;

        private Coroutine _aggroScanCoroutine;
        private Coroutine _pathUpdateCoroutine;
        private Coroutine _patrolWaitCoroutine;
        private Coroutine _regenCoroutine;

        private bool _deathProcessed = false;

        private const float REGEN_INTERVAL        = 3f;
        private const float REGEN_PERCENT         = 0.05f;
        private const float SKILL_RANGE_TOLERANCE = 2.0f;

        // ── Awake / OnStartServer ──────────────────────────────────────────

        private void Awake()
        {
            _agent    = GetComponent<NavMeshAgent>();
            _animator = GetComponentInChildren<Animator>();

            _stats = StatsCalculator.Calculate(
                new BaseAttributes { STR = baseSTR, AGI = baseAGI, VIT = baseVIT,
                                     DEX = baseDEX, INT = baseINT, LUK = baseLUK },
                level, CharacterRace.Human);
        }

        /// <summary>
        /// CORRIGIDO: OnStartServer NÃO chama ServerReset.
        /// ServerReset é chamado por SetSpawnData ou pelo fallback no Update.
        /// </summary>
        public override void OnStartServer()
        {
            _patrolRadiusRuntime = patrolRadius;
        }

        public override void OnStartClient()
        {
            if (selectionIndicator) selectionIndicator.SetActive(false);
            healthBarUI?.UpdateBar(_currentHP, _maxHP);
        }

        /// <summary>
        /// Chamado pelo NetworkMonsterSpawner ANTES de NetworkServer.Spawn.
        /// Define _homePosition e dispara ServerReset após o spawn completar.
        /// </summary>
        [Server]
        public void SetSpawnData(Vector3 homePos, float newPatrolRadius)
        {
            _homePosition        = homePos;
            _patrolRadiusRuntime = newPatrolRadius;
            transform.position   = homePos;
            _patrolTargetSet     = false;

            // OnStartServer já rodou durante Spawn — podemos chamar ServerReset agora
            // Se por algum motivo não rodou ainda, o Update fará o fallback
            StartCoroutine(ServerResetNextFrame());
        }

        /// <summary>
        /// Aguarda 1 frame para garantir que OnStartServer() completou antes de resetar.
        /// </summary>
        [Server]
        private IEnumerator ServerResetNextFrame()
        {
            yield return null;
            if (!_serverResetDone)
                ServerReset();
        }

        [Server]
        private void ServerReset()
        {
            _serverResetDone = true;
            _maxHP           = _stats.MaxHP;
            _currentHP       = _maxHP;
            _isDead          = false;
            _deathProcessed  = false;
            _wasAttacked     = false;
            _state           = AIState.Patrol;
            _aggroTarget     = null;
            _attackTimer     = 0f;
            _fleeTimer       = 0f;
            _patrolIndex     = 0;
            _patrolWaiting   = false;
            _patrolTargetSet = false;
            _damageLog.Clear();

            if (_agent != null)
            {
                _agent.enabled = true;
                _agent.speed   = _stats.ASPD;
                if (_agent.isOnNavMesh)
                    _agent.Warp(_homePosition);
                else
                    transform.position = _homePosition;
            }
            else
            {
                transform.position = _homePosition;
            }

            StopAllCoroutines();
            _patrolWaitCoroutine = null;
            _regenCoroutine      = null;

            _aggroScanCoroutine  = StartCoroutine(AggroScanLoop());
            _pathUpdateCoroutine = StartCoroutine(PathUpdateLoop());

            RpcOnRespawned();
        }

        // ── Update ─────────────────────────────────────────────────────────

        private void Update()
        {
            if (!isServer) return;

            // Fallback para monstros colocados manualmente na cena (sem SetSpawnData)
            if (!_serverResetDone)
            {
                _homePosition        = transform.position;
                _patrolRadiusRuntime = patrolRadius;
                ServerReset();
                return;
            }

            if (_isDead) return;

            _attackTimer += Time.deltaTime;

            switch (_state)
            {
                case AIState.Idle:    break;
                case AIState.Patrol:  if (usePatrolPoints) ServerPatrolWaypoints(); break;
                case AIState.Chase:   ServerChaseCheck();  break;
                case AIState.Combat:  ServerCombat();      break;
                case AIState.Flee:    ServerFleeCheck();   break;
                case AIState.ReturnHome: ServerReturnHomeCheck(); break;
            }
        }

        // ── Coroutines de IA ───────────────────────────────────────────────

        [Server]
        private IEnumerator AggroScanLoop()
        {
            var wait = new WaitForSeconds(aggroScanInterval);
            while (true)
            {
                if (!_isDead &&
                    (_state == AIState.Idle || _state == AIState.Patrol) &&
                    disposition != MonsterDisposition.Passive &&
                    !(disposition == MonsterDisposition.Neutral && !_wasAttacked))
                {
                    TryAggro();
                }
                yield return wait;
            }
        }

        [Server]
        private IEnumerator PathUpdateLoop()
        {
            // Tick imediato para iniciar patrulha sem esperar pathUpdateRate
            yield return null;

            while (true)
            {
                if (!_isDead)
                {
                    switch (_state)
                    {
                        case AIState.Chase:      UpdateChasePath();      break;
                        case AIState.ReturnHome: UpdateReturnHomePath(); break;
                        case AIState.Flee:       UpdateFleePath();       break;
                        case AIState.Patrol:
                            if (!usePatrolPoints) UpdatePatrolAreaPath();
                            break;
                    }
                }
                yield return new WaitForSeconds(pathUpdateRate);
            }
        }

        [Server]
        private IEnumerator PatrolWaitCoroutine()
        {
            _patrolWaiting = true;
            yield return new WaitForSeconds(patrolWaitTime);
            _patrolWaiting       = false;
            _patrolTargetSet     = false;
            _patrolWaitCoroutine = null;
        }

        [Server]
        private IEnumerator RegenLoop()
        {
            var wait = new WaitForSeconds(REGEN_INTERVAL);
            while (_state == AIState.ReturnHome)
            {
                yield return wait;
                if (_state != AIState.ReturnHome) break;
                _currentHP = Mathf.Min(_maxHP, _currentHP + _maxHP * REGEN_PERCENT);
            }
            _regenCoroutine = null;
        }

        // ── Estados de IA ──────────────────────────────────────────────────

        private void ServerPatrolWaypoints()
        {
            if (patrolPoints == null || patrolPoints.Length == 0) return;
            if (!_agent.isOnNavMesh) return;

            if (_patrolWaiting)
            {
                _fleeTimer += Time.deltaTime;
                if (_fleeTimer < patrolWaitTime) return;
                _patrolWaiting = false;
                _fleeTimer     = 0f;
            }

            if (!_agent.pathPending && _agent.remainingDistance < 0.5f)
            {
                _patrolIndex = (_patrolIndex + 1) % patrolPoints.Length;
                _agent.SetDestination(patrolPoints[_patrolIndex].position);
                _patrolWaiting = true;
                _fleeTimer     = 0f;
            }
        }

        private void ServerChaseCheck()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            if (Vector3.Distance(transform.position, _homePosition) > leashRange)
            { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);
            if (dist > aggroRange * 2.5f) { ResetAggro(); return; }

            if (dist <= attackRange)
            {
                _attackTimer = 0f;
                _state       = AIState.Combat;
                if (_agent.isOnNavMesh) _agent.ResetPath();
            }
        }

        private void ServerCombat()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) { ResetAggro(); return; }
            if (Vector3.Distance(transform.position, _homePosition) > leashRange)
            { ResetAggro(); EnterReturnHome(); return; }

            float dist = Vector3.Distance(transform.position, _aggroTarget.transform.position);

            if (dist > attackRange * 1.4f) { _state = AIState.Chase; return; }

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
                    transform.rotation, Quaternion.LookRotation(dir), 8f * Time.deltaTime);

            if (_attackTimer >= attackCooldown)
            {
                _attackTimer = 0f;
                ServerAttack();
            }
        }

        private void ServerFleeCheck()
        {
            _fleeTimer += Time.deltaTime;
            if (_fleeTimer >= fleeDuration || !_agent.isOnNavMesh)
            {
                if (_agent != null) _agent.speed = _stats.ASPD;
                _fleeTimer = 0f;
                EnterReturnHome();
            }
        }

        private void ServerReturnHomeCheck()
        {
            if (!_agent.isOnNavMesh) return;
            if (Vector3.Distance(transform.position, _homePosition) < 1.5f)
            {
                _agent.ResetPath();
                _wasAttacked     = false;
                _patrolWaiting   = false;
                _patrolTargetSet = false;
                _damageLog.Clear();
                if (_regenCoroutine != null) { StopCoroutine(_regenCoroutine); _regenCoroutine = null; }
                _state = AIState.Patrol;
            }
        }

        // ── Paths ──────────────────────────────────────────────────────────

        [Server]
        private void UpdateChasePath()
        {
            if (_aggroTarget == null || !_agent.isOnNavMesh) return;
            _agent.stoppingDistance = attackRange * 0.85f;
            _agent.SetDestination(_aggroTarget.transform.position);
        }

        [Server]
        private void UpdateReturnHomePath()
        {
            if (!_agent.isOnNavMesh) return;
            _agent.stoppingDistance = 0.5f;
            _agent.SetDestination(_homePosition);
        }

        [Server]
        private void UpdateFleePath()
        {
            if (_aggroTarget == null || !_agent.isOnNavMesh) return;
            Vector3 fleeDir = (transform.position - _aggroTarget.transform.position).normalized;
            Vector3 fleePos = transform.position + fleeDir * (aggroRange * 1.5f);
            if (NavMesh.SamplePosition(fleePos, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        [Server]
        private void UpdatePatrolAreaPath()
        {
            if (!_agent.isOnNavMesh) return;
            if (_patrolWaiting) return;

            bool arrived = !_agent.pathPending && _agent.remainingDistance < 0.6f;

            if (_patrolTargetSet && arrived)
            {
                if (_patrolWaitCoroutine == null)
                    _patrolWaitCoroutine = StartCoroutine(PatrolWaitCoroutine());
                return;
            }

            if (!_patrolTargetSet)
            {
                if (TryGetRandomAreaPoint(_homePosition, _patrolRadiusRuntime, out Vector3 dest))
                {
                    _agent.SetDestination(dest);
                    _currentPatrolTarget = dest;
                    _patrolTargetSet     = true;
                }
            }
        }

        // ── Aggro ──────────────────────────────────────────────────────────

        [Server]
        private void TryAggro()
        {
            float closest = aggroRange;
            NetworkPlayer found = null;
            foreach (var np in NetworkPlayer.All)
            {
                if (np == null || np.Dead) continue;
                float d = Vector3.Distance(transform.position, np.transform.position);
                if (d < closest) { closest = d; found = np; }
            }
            if (found != null)
            {
                _aggroTarget = found;
                _state       = AIState.Chase;
                _attackTimer = 0f;
                if (_patrolWaitCoroutine != null)
                {
                    StopCoroutine(_patrolWaitCoroutine);
                    _patrolWaitCoroutine = null;
                }
                _patrolWaiting   = false;
                _patrolTargetSet = false;
            }
        }

        [Server]
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

            if (Vector3.Distance(transform.position, _homePosition) > leashRange * 0.5f)
                EnterReturnHome();
            else { _patrolWaiting = false; _state = AIState.Patrol; }
        }

        [Server]
        private void EnterReturnHome()
        {
            _state       = AIState.ReturnHome;
            _aggroTarget = null;
            if (_patrolWaitCoroutine != null)
            {
                StopCoroutine(_patrolWaitCoroutine);
                _patrolWaitCoroutine = null;
            }
            _patrolWaiting = false;
            if (_agent != null) _agent.stoppingDistance = 0.5f;
            if (_regenCoroutine != null) StopCoroutine(_regenCoroutine);
            _regenCoroutine = StartCoroutine(RegenLoop());
        }

        // ── Ataque ─────────────────────────────────────────────────────────

        [Server]
        private void ServerAttack()
        {
            if (_aggroTarget == null || _aggroTarget.Dead) return;
            bool hit = StatsCalculator.RollHit(_stats.HIT, _aggroTarget.ServerStats?.FLEE ?? 20f);
            if (!hit) { RpcShowMiss(_aggroTarget.transform.position); return; }
            bool  crit = StatsCalculator.RollCrit(_stats.CRIT);
            float dmg  = StatsCalculator.CalculatePhysicalDamage(
                _stats.ATK, _aggroTarget.ServerStats?.DEF ?? 10f, crit, _stats.CritDMG);
            _aggroTarget.ServerApplyDamage(dmg);
            RpcPlayAnim("Attack");
        }

        [Server]
        private void ApplyDamageInternal(float dmg)
        {
            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            if (_currentHP <= 0f) ServerDie();
        }

        // ── CmdRequestSkill ────────────────────────────────────────────────

        [Command(requiresAuthority = false)]
        public void CmdRequestSkill(uint attackerNetId, int skillIndex, bool isPhysical)
        {
            if (_isDead) return;
            NetworkPlayer attacker = null;
            foreach (var np in NetworkPlayer.All)
                if (np != null && np.netId == attackerNetId) { attacker = np; break; }
            if (attacker == null || attacker.Dead) return;

            var atkStats = attacker.ServerStats;
            if (atkStats == null) return;

            var skill = attacker.GetComponent<SkillSystem>()?.GetSkill(skillIndex);
            if (skill == null) { attacker.RpcSkillRejected(skillIndex, "Skill inválida."); return; }

            if (!attacker.ServerCheckAndSetCooldown(skillIndex, skill.Cooldown))
            {
                attacker.RpcSkillRejected(skillIndex, $"{skill.Name}: ainda em cooldown.");
                return;
            }
            if (attacker.CurrentMP < skill.ManaCost)
            {
                attacker.RpcSkillRejected(skillIndex, "MP insuficiente!");
                return;
            }

            attacker.ServerConsumeMP(skill.ManaCost);
            ServerTakeDamageFromPlayer(attacker, atkStats, skillIndex, isPhysical, skill);
            attacker.RpcSkillConfirmed(skillIndex, skill.Cooldown);
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
                ? StatsCalculator.CalculatePhysicalDamage(atkStats.ATK * skill.AtkMultiplier, _stats.DEF, crit, atkStats.CritDMG)
                : StatsCalculator.CalculateMagicDamage(atkStats.MATK * skill.AtkMultiplier, _stats.MDEF, crit, atkStats.CritDMG);

            dmg = Mathf.Max(1f, dmg);
            if (!_damageLog.ContainsKey(attacker.netId)) _damageLog[attacker.netId] = 0f;
            _damageLog[attacker.netId] += dmg;

            _currentHP = Mathf.Max(0f, _currentHP - dmg);
            RpcShowDamage(dmg, crit, transform.position);

            switch (disposition)
            {
                case MonsterDisposition.Passive:
                    if (_state != AIState.Flee && _state != AIState.ReturnHome && _state != AIState.Dead)
                    {
                        _aggroTarget = attacker; _fleeTimer = 0f; _state = AIState.Flee;
                        if (_agent != null) _agent.speed = _stats.ASPD * fleeSpeedMult;
                    }
                    break;
                case MonsterDisposition.Neutral:
                    _wasAttacked = true;
                    if (_state == AIState.Idle || _state == AIState.Patrol || _state == AIState.ReturnHome)
                    {
                        CancelPatrolWait();
                        _aggroTarget = attacker; _state = AIState.Chase; _attackTimer = 0f;
                    }
                    break;
                case MonsterDisposition.Aggressive:
                    if (_state == AIState.Idle || _state == AIState.Patrol)
                    {
                        CancelPatrolWait();
                        _aggroTarget = attacker; _state = AIState.Chase; _attackTimer = 0f;
                    }
                    break;
            }

            if (_currentHP <= 0f) ServerDie();
        }

        private void CancelPatrolWait()
        {
            if (_patrolWaitCoroutine != null) { StopCoroutine(_patrolWaitCoroutine); _patrolWaitCoroutine = null; }
            _patrolWaiting = false;
        }

        // ── Morte / Respawn ────────────────────────────────────────────────

        [Server]
        private void ServerDie()
        {
            if (_isDead || _deathProcessed) return;
            _isDead = true; _deathProcessed = true; _state = AIState.Dead;
            StopAllCoroutines();
            _aggroScanCoroutine = _pathUpdateCoroutine = _patrolWaitCoroutine = _regenCoroutine = null;
            if (_agent != null) { if (_agent.isOnNavMesh) _agent.ResetPath(); _agent.enabled = false; }
            Debug.Log("[NetworkMonster] Monstro morreu!");
            ServerDistributeExp();
            StartCoroutine(ServerDeathSequence());
        }

        [Server]
        private void ServerDistributeExp()
        {
            if (_damageLog.Count == 0) return;
            float total = 0f;
            foreach (var kv in _damageLog) total += kv.Value;
            if (total <= 0f) return;
            foreach (var kv in _damageLog)
            {
                long xp = (long)Mathf.Max(1f, expReward * (kv.Value / total));
                foreach (var np in NetworkPlayer.All)
                    if (np != null && np.netId == kv.Key) { np.ServerGrantExp(xp); break; }
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
            if (isServer) { _serverResetDone = false; ServerReset(); }
        }

        // ── NavMesh Helper ─────────────────────────────────────────────────

        private bool TryGetRandomAreaPoint(Vector3 center, float radius, out Vector3 result)
        {
            for (int i = 0; i < 15; i++)
            {
                Vector2 r2 = Random.insideUnitCircle * radius;
                Vector3 c  = center + new Vector3(r2.x, 0f, r2.y);
                if (NavMesh.SamplePosition(c, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                { result = hit.position; return true; }
            }
            result = center; return false;
        }

        // ── ClientRpcs ─────────────────────────────────────────────────────

        [ClientRpc] private void RpcShowDamage(float dmg, bool crit, Vector3 pos)
            => FloatingTextManager.Instance?.Show(crit ? $"CRÍTICO! {dmg:0}" : $"{dmg:0}", pos + Vector3.up, crit ? Color.yellow : Color.white);

        [ClientRpc] private void RpcShowMiss(Vector3 pos)
            => FloatingTextManager.Instance?.Show("MISS", pos + Vector3.up * 0.5f, Color.gray);

        [ClientRpc] private void RpcPlayAnim(string trigger)
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
            if (visualRoot)         visualRoot.SetActive(false);
            if (selectionIndicator) selectionIndicator.SetActive(false);
            if (healthBarUI)        healthBarUI.gameObject.SetActive(false);
        }

        [ClientRpc]
        private void RpcOnRespawned()
        {
            if (visualRoot)         visualRoot.SetActive(true);
            if (selectionIndicator) selectionIndicator.SetActive(false);
            if (healthBarUI)        { healthBarUI.gameObject.SetActive(true); healthBarUI.UpdateBar(_currentHP, _maxHP); }
        }

        private void OnCurrentHPChanged(float _, float v) => healthBarUI?.UpdateBar(v, _maxHP);
        private void OnDeadChanged(bool _, bool dead) { if (dead && _agent != null) _agent.enabled = false; }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = disposition switch { MonsterDisposition.Passive => Color.green, MonsterDisposition.Neutral => Color.yellow, _ => Color.red };
            Gizmos.DrawWireSphere(transform.position, aggroRange);
            Gizmos.color = new Color(1f, 0.3f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, attackRange);
            Gizmos.color = Color.blue;
            Gizmos.DrawWireSphere(transform.position, kiteDistance);
            Gizmos.color = new Color(1f, 1f, 1f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, leashRange);
        }
#endif
    }
}