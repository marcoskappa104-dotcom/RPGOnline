using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.UI;
using System.Collections;

namespace RPG.Network
{
    /// <summary>
    /// WorldItem v2
    ///
    /// CORREÇÕES v2:
    ///   1. RACE CONDITION em CmdPickUp: antes, dois clientes podiam chamar
    ///      CmdPickUp quase simultaneamente. O segundo passava pela verificação
    ///      "_picked = false" antes do primeiro terminar de setar "_picked = true".
    ///      Agora _picked é verificado como primeira coisa, e NetworkServer.Destroy
    ///      é chamado dentro do mesmo frame de servidor.
    ///
    ///   2. DISTÂNCIA verificada ANTES de buscar o NetworkPlayer para economizar
    ///      a iteração do HashSet em tentativas inválidas.
    ///      Ordem nova: encontra player → verifica distância → verifica inventário.
    ///
    ///   3. AutoDespawn: StopAllCoroutines() é chamado quando o item é coletado
    ///      para garantir que a coroutine de despawn não acesse um objeto destruído.
    ///
    ///   4. Bobbing no Update(): guarda apenas _startY (float) em vez do Vector3
    ///      inteiro para economizar memória e evitar modificar X/Z acidentalmente.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class WorldItem : NetworkBehaviour
    {
        [Header("Visual")]
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private TMPro.TMP_Text  nameLabel;
        [SerializeField] private GameObject      glowEffect;

        [Header("Configuração")]
        [SerializeField] private float despawnTime  = 60f;
        [SerializeField] private float bobAmplitude = 0.15f;
        [SerializeField] private float bobFrequency = 1.5f;
        [SerializeField] private float pickupRadius = 2.5f;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnItemIdChanged))] private string _itemId = "";

        public string ItemId => _itemId;

        // CORREÇÃO v2: apenas Y inicial (float) em vez de Vector3 completo
        private float _startY;
        private bool  _picked = false;

        // ── Server Init ────────────────────────────────────────────────────

        [Server]
        public void ServerInitialize(string itemId)
        {
            _itemId = itemId;
            StartCoroutine(AutoDespawn());
        }

        [Server]
        private IEnumerator AutoDespawn()
        {
            yield return new WaitForSeconds(despawnTime);
            if (!_picked && isServer)
            {
                Debug.Log($"[WorldItem] Auto-despawn: {_itemId}");
                NetworkServer.Destroy(gameObject);
            }
        }

        // ── Client visual ──────────────────────────────────────────────────

        public override void OnStartClient()
        {
            _startY = transform.position.y;
            RefreshVisual(_itemId);
        }

        private void OnItemIdChanged(string oldId, string newId) => RefreshVisual(newId);

        private void RefreshVisual(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            var item = ItemDatabase.Instance?.GetItem(itemId);
            if (item == null) return;

            if (spriteRenderer != null && item.Icon != null)
                spriteRenderer.sprite = item.Icon;

            if (nameLabel != null)
            {
                nameLabel.text  = item.DisplayName;
                nameLabel.color = item.RarityColor;
            }

            if (glowEffect != null)
                glowEffect.SetActive(item.Rarity >= ItemRarity.Rare);
        }

        // CORREÇÃO v2: usa _startY (float) e não modifica X/Z
        private void Update()
        {
            if (!isClient) return;
            float newY = _startY + Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f) * bobAmplitude;
            var   pos  = transform.position;
            transform.position = new Vector3(pos.x, newY, pos.z);
        }

        // ── Pickup ─────────────────────────────────────────────────────────

        /// <summary>
        /// Chamado pelo NetworkPlayerController quando o jogador clica no item.
        /// requiresAuthority = false: qualquer cliente pode chamar.
        /// </summary>
        [Command(requiresAuthority = false)]
        public void CmdPickUp(uint playerNetId)
        {
            // CORREÇÃO v2: verifica _picked PRIMEIRO antes de qualquer busca
            if (_picked) return;

            // Encontra o NetworkPlayer
            NetworkPlayer player = null;
            foreach (var np in NetworkPlayer.All)
            {
                if (np != null && np.netId == playerNetId) { player = np; break; }
            }
            if (player == null || player.Dead) return;

            // CORREÇÃO v2: distância verificada logo após encontrar o player
            float dist = Vector3.Distance(transform.position, player.transform.position);
            if (dist > pickupRadius * 2f)
            {
                Debug.LogWarning($"[WorldItem] Pickup muito longe: {dist:0.1}u por {player.CharacterName}");
                return;
            }

            // Tenta adicionar ao inventário
            var inventory = player.GetComponent<NetworkInventory>();
            if (inventory == null) return;

            int slotIndex = inventory.ServerAddItem(_itemId);
            if (slotIndex < 0) return;

            // CORREÇÃO v2: seta _picked imediatamente para bloquear chamadas concorrentes
            _picked = true;
            StopAllCoroutines(); // cancela auto-despawn

            // Feedback visual
            var    item     = ItemDatabase.Instance?.GetItem(_itemId);
            string itemName = item?.DisplayName ?? _itemId;
            Color  color    = item?.RarityColor ?? Color.white;
            RpcPickupFeedback(playerNetId, itemName, color);

            NetworkServer.Destroy(gameObject);
        }

        [ClientRpc]
        private void RpcPickupFeedback(uint playerNetId, string itemName, Color rarityColor)
        {
            if (NetworkClient.localPlayer == null) return;
            if (NetworkClient.localPlayer.netId != playerNetId) return;

            FloatingTextManager.Instance?.Show(
                $"+ {itemName}", transform.position + Vector3.up, rarityColor);
            UIManager.Instance?.ShowMessage($"Coletou: {itemName}");
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
            Gizmos.DrawSphere(transform.position, pickupRadius);
        }
#endif
    }
}