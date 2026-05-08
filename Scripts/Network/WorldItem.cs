using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.UI;
using System.Collections;

namespace RPG.Network
{
    /// <summary>
    /// WorldItem v1 — Item físico spawnado no chão quando um monstro morre.
    ///
    /// FLUXO:
    ///   Monstro morre → ItemDropManager.ServerSpawnDrop() → Instantiate(worldItemPrefab)
    ///   → WorldItem.ServerInitialize(itemId) → NetworkServer.Spawn()
    ///
    ///   Jogador clica no item → CmdPickUp() → servidor valida
    ///   → add ao NetworkInventory → NetworkServer.Destroy(worldItem)
    ///
    /// PREFAB:
    ///   - NetworkIdentity
    ///   - WorldItem
    ///   - Collider (IsTrigger = false para raycast, ou trigger para auto-pickup)
    ///   - SpriteRenderer ou MeshRenderer com ícone/modelo 3D
    ///
    /// AUTO-DESTROY: se ninguém coletar em despawnTime segundos, some automaticamente.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class WorldItem : NetworkBehaviour
    {
        [Header("Visual")]
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private TMPro.TMP_Text  nameLabel;
        [SerializeField] private GameObject      glowEffect;

        [Header("Configuração")]
        [SerializeField] private float despawnTime    = 60f;
        [SerializeField] private float bobAmplitude   = 0.15f;
        [SerializeField] private float bobFrequency   = 1.5f;
        [SerializeField] private float pickupRadius   = 2.5f;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnItemIdChanged))] private string _itemId = "";

        public string ItemId => _itemId;

        private Vector3 _startPos;
        private bool    _picked = false;

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
                NetworkServer.Destroy(gameObject);
        }

        // ── Client visual ──────────────────────────────────────────────────

        public override void OnStartClient()
        {
            _startPos = transform.position;
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

            // Glow para itens raros+
            if (glowEffect != null)
                glowEffect.SetActive(item.Rarity >= ItemRarity.Rare);
        }

        private void Update()
        {
            // Animação de bobbing
            if (!isClient) return;
            float y = _startPos.y + Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f) * bobAmplitude;
            transform.position = new Vector3(transform.position.x, y, transform.position.z);
        }

        // ── Pickup ─────────────────────────────────────────────────────────

        /// <summary>
        /// Chamado pelo NetworkPlayerController quando o jogador clica no item.
        /// </summary>
        [Command(requiresAuthority = false)]
        public void CmdPickUp(uint playerNetId)
        {
            if (_picked) return;

            // Encontra o NetworkPlayer
            NetworkPlayer player = null;
            foreach (var np in NetworkPlayer.All)
            {
                if (np != null && np.netId == playerNetId) { player = np; break; }
            }
            if (player == null || player.Dead) return;

            // Verifica distância (anti-cheat)
            float dist = Vector3.Distance(transform.position, player.transform.position);
            if (dist > pickupRadius * 2f)
            {
                Debug.LogWarning($"[WorldItem] Pickup muito longe: {dist:0.1}u para {player.CharacterName}");
                return;
            }

            var inventory = player.GetComponent<NetworkInventory>();
            if (inventory == null) return;

            int slotIndex = inventory.ServerAddItem(_itemId);
            if (slotIndex < 0) return;

            _picked = true;

            // Feedback visual no cliente do jogador
            var item = ItemDatabase.Instance?.GetItem(_itemId);
            string itemName = item?.DisplayName ?? _itemId;
            RpcPickupFeedback(playerNetId, itemName, item?.RarityColor ?? Color.white);

            NetworkServer.Destroy(gameObject);
        }

        [ClientRpc]
        private void RpcPickupFeedback(uint playerNetId, string itemName, Color rarityColor)
        {
            // Mostra feedback apenas para o jogador que coletou
            if (NetworkClient.localPlayer == null) return;
            if (NetworkClient.localPlayer.netId != playerNetId) return;

            FloatingTextManager.Instance?.Show(
                $"+ {itemName}", transform.position + Vector3.up, rarityColor);
            UIManager.Instance?.ShowMessage($"Coletou: {itemName}");
        }

        // ── Gizmo de pickup radius ─────────────────────────────────────────
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
            Gizmos.DrawSphere(transform.position, pickupRadius);
        }
#endif
    }
}