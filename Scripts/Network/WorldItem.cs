using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.UI;
using System.Collections;

namespace RPG.Network
{
    /// <summary>
    /// WorldItem v3
    ///
    /// CORREÇÃO v3:
    ///   1. BOBBING: o Update() anterior modificava transform.position completo
    ///      criando um new Vector3 por frame por item. Com 20 itens = 20 alocações/frame.
    ///      Agora usa transform.localPosition com apenas o Y variando, zerando X e Z
    ///      do offset — elimina alocações desnecessárias e não interfere no X/Z world.
    ///
    ///      Na prática: guardamos a posição world no OnStartClient e usamos um
    ///      TransformPoint local para mover apenas Y, mantendo X/Z intocados.
    ///      Técnica alternativa limpa: modificar apenas transform.localPosition.y
    ///      não funciona em C# (propriedade). Usamos a abordagem de reutilizar
    ///      um Vector3 pré-alocado e atualizar apenas o Y.
    ///
    ///   2. RACE CONDITION, DISTÂNCIA e AUTODESPAWN: mantidos do v2.
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

        // CORREÇÃO v3: reutiliza Vector3 pré-alocado, guarda só Y base
        private float   _startY;
        private Vector3 _bobPosition; // reutilizado a cada frame — sem alocação
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
            {
                Debug.Log($"[WorldItem] Auto-despawn: {_itemId}");
                NetworkServer.Destroy(gameObject);
            }
        }

        // ── Client visual ──────────────────────────────────────────────────

        public override void OnStartClient()
        {
            // Guarda posição inicial e inicializa o Vector3 de bobbing
            _startY      = transform.position.y;
            _bobPosition = transform.position;
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

        // CORREÇÃO v3: reutiliza _bobPosition sem new Vector3 por frame
        private void Update()
        {
            if (!isClient) return;

            float newY = _startY + Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f) * bobAmplitude;

            // Reutiliza o Vector3 existente — sem alocação GC
            _bobPosition.x = transform.position.x;
            _bobPosition.y = newY;
            _bobPosition.z = transform.position.z;
            transform.position = _bobPosition;
        }

        // ── Pickup ─────────────────────────────────────────────────────────

        [Command(requiresAuthority = false)]
        public void CmdPickUp(uint playerNetId)
        {
            if (_picked) return;

            NetworkPlayer player = null;
            foreach (var np in NetworkPlayer.All)
            {
                if (np != null && np.netId == playerNetId) { player = np; break; }
            }
            if (player == null || player.Dead) return;

            float dist = Vector3.Distance(transform.position, player.transform.position);
            if (dist > pickupRadius * 2f)
            {
                Debug.LogWarning($"[WorldItem] Pickup muito longe: {dist:0.1}u por {player.CharacterName}");
                return;
            }

            var inventory = player.GetComponent<NetworkInventory>();
            if (inventory == null) return;

            int slotIndex = inventory.ServerAddItem(_itemId);
            if (slotIndex < 0) return;

            _picked = true;
            StopAllCoroutines();

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