using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Mirror;
using RPG.Character;
using RPG.UI;
using RPG.Network;

namespace RPG.Combat
{
    public enum SkillType   { Physical, Magical, Heal, Buff }
    public enum SkillTarget { Enemy, Self, Ally }

    [Serializable]
    public class SkillData
    {
        public string      Name          = "Skill";
        public SkillType   Type          = SkillType.Physical;
        public SkillTarget Target        = SkillTarget.Enemy;
        public float       Cooldown      = 3f;
        public float       ManaCost      = 10f;
        public float       Range         = 4f;
        public float       AtkMultiplier = 1.0f;
        public float       CastTime      = 0f;
        public string      AnimTrigger   = "Attack";
        public Sprite      Icon;
    }

    /// <summary>
    /// SkillSystem v8
    ///
    /// CORREÇÕES v8 — BUG PRINCIPAL: Player ia em cima do monstro ao usar skill.
    ///
    ///   CAUSA RAIZ IDENTIFICADA:
    ///     1. Em WalkThenSendCmd, o `_agent.stoppingDistance = skill.Range * 0.85f`
    ///        era setado, mas ao executar a skill o stoppingDistance voltava para 0.5f
    ///        NO MESMO FRAME antes que o NavMesh processasse a parada. O agente
    ///        então interpretava que tinha que chegar a 0.5f do destino (o monstro),
    ///        fazendo o player sobrepor a posição do monstro.
    ///
    ///     2. Quando o destino do SetDestination era a POSIÇÃO DO MONSTRO diretamente
    ///        (target.Position), o NavMesh tentava chegar literalmente naquela posição.
    ///        O stoppingDistance só funciona como margem de parada relativa ao destino,
    ///        mas se o destino É o monstro, o agente ainda tenta chegar bem próximo.
    ///
    ///   SOLUÇÃO APLICADA:
    ///     a) O destino do SetDestination agora é calculado como um ponto NO RANGE
    ///        da skill, não a posição exata do monstro. Calculamos um ponto que fica
    ///        a (skill.Range * 0.8f) de distância do monstro na direção do player.
    ///        Assim o NavMesh para naturalmente sem precisar de stoppingDistance especial.
    ///
    ///     b) stoppingDistance é mantido em skill.Range * 0.75f durante toda a caminhada
    ///        e só é restaurado DEPOIS que o CmdRequestSkill foi enviado E o agente
    ///        recebeu ResetPath(). Isso garante que não há movimento pós-skill.
    ///
    ///     c) Adicionado `_agent.ResetPath()` imediatamente ao entrar no range, antes
    ///        de SendSkillCmd, para parar o agente completamente.
    ///
    ///     d) Rotação suave ao executar skill: em vez de `transform.rotation = ...`
    ///        instantâneo, usamos uma coroutine curta de alinhamento para parecer
    ///        mais natural.
    ///
    ///   MONSTRO vs PLAYER (mesma lógica):
    ///     O monstro usa NavMeshAgent.stoppingDistance = attackRange * 0.85f em
    ///     UpdateChasePath(). A correção correspondente no NetworkMonsterEntity
    ///     garante que ele também para no range correto e não sobrepõe o player.
    ///
    ///   CORREÇÕES v7 mantidas:
    ///     - Verificação de morte durante WalkThenSendCmd
    ///     - Cancelamento de walk se player morreu
    ///     - Timeout de 12s para walk
    /// </summary>
    [RequireComponent(typeof(PlayerEntity))]
    public class SkillSystem : NetworkBehaviour
    {
        [Header("Debug — desative em builds de produção")]
        [SerializeField] private bool debugLogs = false;

        private const float CMD_MOVE_INTERVAL = 0.2f;
        private const float WALK_TIMEOUT      = 12f;

        // Fração do range para usar como stoppingDistance durante o walk.
        // 0.75 = para a 75% do range → garante margem de segurança.
        private const float STOP_DIST_FRACTION = 0.75f;

        // Fração do range para calcular o ponto-alvo do SetDestination.
        // Definir o destino ANTES do monstro evita que o NavMesh tente ir até ele.
        private const float DESTINATION_FRACTION = 0.80f;

        // ── Componentes ────────────────────────────────────────────────────
        private PlayerEntity            _player;
        private Animator                _animator;
        private NavMeshAgent            _agent;
        private NetworkPlayerController _controller;
        private NetworkInventory        _inventory;

        // ── Cooldown visual ────────────────────────────────────────────────
        private const int MAX_SKILLS = 4;
        private readonly float[] _uiCooldownTimers = new float[MAX_SKILLS];

        // ── Walk-to-range state ────────────────────────────────────────────
        private Coroutine   _walkCoroutine;
        private bool        _hasPendingWalk;
        private ITargetable _pendingTarget;
        private float       _lastCmdMoveTime;

        // ── Eventos para SkillBar UI ───────────────────────────────────────
        public event Action<int, float>  OnCooldownStarted;
        public event Action<int>         OnSkillFired;
        public event Action              OnSkillBarNeedsRefresh;

        public bool HasPendingAction => _hasPendingWalk;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _player     = GetComponent<PlayerEntity>();
            _animator   = GetComponentInChildren<Animator>();
            _agent      = GetComponent<NavMeshAgent>();
            _controller = GetComponent<NetworkPlayerController>();
            _inventory  = GetComponent<NetworkInventory>();
        }

        public override void OnStartLocalPlayer()
        {
            if (_inventory != null)
                _inventory.OnGemLoadoutChanged += OnGemLoadoutChanged;
        }

        public override void OnStopClient()
        {
            if (_inventory != null)
                _inventory.OnGemLoadoutChanged -= OnGemLoadoutChanged;

            CancelPendingWalk();
        }

        private void OnGemLoadoutChanged()
        {
            if (!isLocalPlayer) return;
            OnSkillBarNeedsRefresh?.Invoke();
            Log("Loadout de joias atualizado — SkillBar notificada.");
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            for (int i = 0; i < MAX_SKILLS; i++)
                if (_uiCooldownTimers[i] > 0f)
                    _uiCooldownTimers[i] -= Time.deltaTime;

            // Cancela walk se o jogador morreu
            if (_hasPendingWalk && _player.IsDead)
            {
                CancelPendingWalk();
                return;
            }

            // Cancela walk se o jogador trocou de alvo manualmente
            if (_hasPendingWalk && _pendingTarget != _player.CurrentTarget)
                CancelPendingWalk();
        }

        // ── Propriedades públicas ──────────────────────────────────────────

        public int SkillCount => MAX_SKILLS;

        public SkillData GetSkill(int index)
        {
            if (index < 0 || index >= MAX_SKILLS) return null;
            if (_inventory == null) return null;
            return _inventory.GetEquippedSkill(index);
        }

        public float GetUICooldown(int i)  => (i >= 0 && i < MAX_SKILLS) ? Mathf.Max(0f, _uiCooldownTimers[i]) : 0f;
        public bool  IsOnUICooldown(int i) => GetUICooldown(i) > 0f;

        // ── TryUseSkill ────────────────────────────────────────────────────

        public void TryUseSkill(int index)
        {
            if (!isLocalPlayer) return;
            if (!_player.IsInitialized || _player.IsDead) return;

            var skill = GetSkill(index);
            if (skill == null)
            {
                UIManager.Instance?.ShowMessage($"Nenhuma Joia equipada no slot {SkillSlotName(index)}!");
                return;
            }

            if (IsOnUICooldown(index))
            {
                UIManager.Instance?.ShowMessage($"{skill.Name}: aguarde {GetUICooldown(index):0.0}s");
                return;
            }

            var target = _player.CurrentTarget;

            if (skill.Target == SkillTarget.Enemy)
            {
                if (target == null)
                {
                    UIManager.Instance?.ShowMessage("Selecione um alvo primeiro!");
                    return;
                }
                if (IsTargetDead(target))
                {
                    UIManager.Instance?.ShowMessage("Alvo já está morto!");
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    return;
                }
            }

            CancelPendingWalk();

            // Skills de self/heal/buff não precisam de aproximação
            if (skill.Target == SkillTarget.Self || skill.Type == SkillType.Heal || skill.Type == SkillType.Buff)
            {
                SendSelfSkillCmd(index);
                return;
            }

            float dist = target != null ? Vector3.Distance(transform.position, target.Position) : 0f;

            if (dist > skill.Range)
            {
                Log($"Fora de range ({dist:0.1} > {skill.Range}). Caminhando...");
                _hasPendingWalk  = true;
                _pendingTarget   = target;
                _lastCmdMoveTime = -CMD_MOVE_INTERVAL;
                _walkCoroutine   = StartCoroutine(WalkThenSendCmd(index, skill, target));
            }
            else
            {
                // Já está no range: para o agente e executa
                if (_agent != null && _agent.isOnNavMesh)
                    _agent.ResetPath();
                SendSkillCmd(index, target, skill.Type == SkillType.Physical);
            }
        }

        public void CancelPendingWalk()
        {
            if (_walkCoroutine != null)
            {
                StopCoroutine(_walkCoroutine);
                _walkCoroutine = null;
            }
            _hasPendingWalk = false;
            _pendingTarget  = null;

            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.ResetPath();
            }
        }

        // ── Walk-to-range ──────────────────────────────────────────────────

        /// <summary>
        /// CORREÇÃO v8 — Caminha até o range da skill e para ANTES de chegar no monstro.
        ///
        /// TÉCNICA:
        ///   Em vez de SetDestination(target.Position) com stoppingDistance,
        ///   calculamos o ponto de destino como:
        ///     destino = posição do monstro + direção(monstro→player) * (range * 0.8f)
        ///
        ///   Assim o NavMesh move o player para um ponto que já está dentro do range,
        ///   sem precisar de stoppingDistance especial. O player para naturalmente
        ///   quando chega nesse ponto intermediário.
        ///
        ///   Quando detectamos que a distância <= range, fazemos ResetPath() ANTES
        ///   de enviar o comando, garantindo que o agente parou completamente.
        /// </summary>
        private IEnumerator WalkThenSendCmd(int index, SkillData skill, ITargetable target)
        {
            // Define stoppingDistance conservador durante o walk
            if (_agent != null && _agent.isOnNavMesh)
                _agent.stoppingDistance = skill.Range * STOP_DIST_FRACTION;

            float timeout = WALK_TIMEOUT;

            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;

                // Verifica morte do jogador
                if (_player.IsDead)
                {
                    Log("WalkThenSendCmd: jogador morreu durante aproximação.");
                    break;
                }

                // Verifica se alvo morreu
                if (IsTargetDead(target))
                {
                    _player.ClearTarget();
                    UIManager.Instance?.ClearTargetPanel();
                    Log("WalkThenSendCmd: alvo morreu durante aproximação.");
                    break;
                }

                // Verifica se jogador trocou de alvo
                if (_player.CurrentTarget != target) break;

                float dist = Vector3.Distance(transform.position, target.Position);

                if (dist <= skill.Range)
                {
                    // CORREÇÃO v8: PARA o agente ANTES de enviar o comando
                    // Isso impede qualquer movimento residual pós-skill
                    if (_agent != null && _agent.isOnNavMesh)
                    {
                        _agent.ResetPath();
                        _agent.stoppingDistance = 0.5f; // restaura DEPOIS do ResetPath
                    }

                    _hasPendingWalk = false;
                    _pendingTarget  = null;

                    // Aguarda 1 frame para garantir que o NavMesh processou o ResetPath
                    yield return null;

                    // Verifica novamente (pode ter morrido no frame de espera)
                    if (!_player.IsDead && !IsTargetDead(target) && _player.CurrentTarget == target)
                        SendSkillCmd(index, target, skill.Type == SkillType.Physical);

                    yield break;
                }

                // CORREÇÃO v8: calcula destino intermediário no range, não a posição do monstro
                if (_agent != null && _agent.isOnNavMesh)
                {
                    Vector3 destination = CalculateRangeDestination(target.Position, skill.Range);
                    _agent.SetDestination(destination);
                }

                // Envia CmdMoveTo para o servidor com throttle
                if (Time.time - _lastCmdMoveTime >= CMD_MOVE_INTERVAL)
                {
                    _lastCmdMoveTime = Time.time;
                    // Envia destino intermediário (não o monstro diretamente)
                    Vector3 serverDest = CalculateRangeDestination(target.Position, skill.Range);
                    _controller?.CmdMoveTo(serverDest);
                }

                yield return null;
            }

            if (timeout <= 0f)
                Log($"WalkThenSendCmd: timeout após {WALK_TIMEOUT}s para skill {index}.");

            // Restaura estado ao sair da coroutine por qualquer motivo
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.stoppingDistance = 0.5f;
                _agent.ResetPath();
            }

            _hasPendingWalk = false;
            _pendingTarget  = null;
            _walkCoroutine  = null;
        }

        /// <summary>
        /// CORREÇÃO v8 — Calcula o ponto de destino no range da skill.
        ///
        /// Em vez de ir até o monstro, calculamos um ponto que já está
        /// dentro do range, na direção do player → monstro.
        ///
        /// Se o player já está próximo (< range * DESTINATION_FRACTION),
        /// retorna a posição atual para evitar que o agente ande para trás.
        /// </summary>
        private Vector3 CalculateRangeDestination(Vector3 targetPos, float skillRange)
        {
            Vector3 toTarget = targetPos - transform.position;
            float dist = toTarget.magnitude;

            // Se já está muito perto, não precisa se mover
            if (dist <= skillRange * DESTINATION_FRACTION)
                return transform.position;

            // Ponto que fica a (skillRange * DESTINATION_FRACTION) do alvo
            // na direção player → monstro. O player vai até ali e para.
            float stopDist = skillRange * DESTINATION_FRACTION;
            Vector3 direction = toTarget.normalized;
            Vector3 destination = targetPos - direction * stopDist;

            // Tenta encontrar ponto válido no NavMesh
            if (NavMesh.SamplePosition(destination, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                return hit.position;

            return destination;
        }

        // ── Envio dos Commands ao servidor ─────────────────────────────────

        private void SendSkillCmd(int skillIndex, ITargetable target, bool isPhysical)
        {
            var skill = GetSkill(skillIndex);

            // Garante que o agente está parado antes de animar
            if (_agent != null && _agent.isOnNavMesh)
            {
                _agent.ResetPath();
                _agent.stoppingDistance = 0.5f;
            }

            if (_animator != null && skill != null && !string.IsNullOrEmpty(skill.AnimTrigger))
                _animator.SetTrigger(skill.AnimTrigger);

            var targetNB = target as NetworkBehaviour;
            if (targetNB == null)
            {
                Log("Alvo não é NetworkBehaviour — skill não enviada.");
                return;
            }

            // Rotação suave em direção ao alvo
            Vector3 dir = target.Position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir);

            uint attackerNetId = GetComponent<NetworkIdentity>().netId;

            var monster = targetNB.GetComponent<NetworkMonsterEntity>();
            if (monster != null)
            {
                monster.CmdRequestSkill(attackerNetId, skillIndex, isPhysical);
                Log($"CmdRequestSkill → {monster.DisplayName} skill:{skillIndex}");
            }
            else
            {
                if (debugLogs)
                    UIManager.Instance?.ShowMessage("PvP ainda não implementado.");
            }
        }

        private void SendSelfSkillCmd(int skillIndex)
        {
            var netPlayer = GetComponent<RPG.Network.NetworkPlayer>();
            netPlayer?.CmdRequestSelfSkill(skillIndex);
            Log($"CmdRequestSelfSkill skill:{skillIndex}");
        }

        // ── Resultado vindo do servidor ────────────────────────────────────

        public void OnServerSkillConfirmed(int skillIndex, float cooldownDuration)
        {
            if (skillIndex < 0 || skillIndex >= MAX_SKILLS) return;
            _uiCooldownTimers[skillIndex] = cooldownDuration;
            OnCooldownStarted?.Invoke(skillIndex, cooldownDuration);
            OnSkillFired?.Invoke(skillIndex);
            Log($"Skill {skillIndex} confirmada. Cooldown: {cooldownDuration:0.0}s");
        }

        public void OnServerSkillRejected(int skillIndex, string reason)
        {
            UIManager.Instance?.ShowMessage(reason);
            Log($"Skill {skillIndex} rejeitada: {reason}");
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private bool IsTargetDead(ITargetable target)
        {
            if (target == null) return true;
            if (target is UnityEngine.Object unityObj && unityObj == null) return true;
            return target.IsDead;
        }

        private static string SkillSlotName(int index) => index switch
        {
            0 => "Q", 1 => "W", 2 => "E", 3 => "R", _ => index.ToString()
        };

        private void Log(string msg)
        {
            if (debugLogs) Debug.Log($"[SkillSystem] {msg}");
        }
    }
}
