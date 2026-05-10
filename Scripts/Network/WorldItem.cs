using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.UI;
using System.Collections;

namespace RPG.Network
{
    /// <summary>
    /// WorldItem v4
    ///
    /// CORREÇÕES v4:
    ///
    ///   BUG-08 — Bobbing causava jitter em multiplayer:
    ///     O Update() modificava transform.position no transform raiz, que o
    ///     Mirror NetworkTransform sincronizava com o servidor — causando jitter.
    ///     SOLUÇÃO: o bobbing agora é aplicado em _visualRoot (filho), deixando
    ///     o transform raiz intocado para o NetworkTransform.
    ///     Se não houver filho visual configurado, cria um automaticamente.
    ///
    ///   BUG-23 — CmdPickUp iterava NetworkPlayer.All (pode modificar durante iteração):
    ///     Substituído por NetworkServer.spawned[playerNetId] que é O(1) e thread-safe.
    ///
    ///   BUG-17 — FloatingTextManager crashava em servidor dedicado:
    ///     Guards de Application.isBatchMode adicionados onde relevante.
    ///
    ///   Todas as correções v3 mantidas (auto-despawn, distância de pickup).
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class WorldItem : NetworkBehaviour
    {
        [Header("Visual")]
        [SerializeField] private SpriteRenderer spriteRenderer;
        [SerializeField] private TMPro.TMP_Text  nameLabel;
        [SerializeField] private GameObject      glowEffect;

        [Header("Visual Root — filho que recebe o bobbing (não o transform raiz)")]
        [Tooltip("Arraste aqui o filho visual. Se null, será criado automaticamente.")]
        [SerializeField] private Transform _visualRoot;

        [Header("Configuração")]
        [SerializeField] private float despawnTime  = 60f;
        [SerializeField] private float bobAmplitude = 0.15f;
        [SerializeField] private float bobFrequency = 1.5f;
        [SerializeField] private float pickupRadius = 2.5f;

        // ── SyncVars ───────────────────────────────────────────────────────
        [SyncVar(hook = nameof(OnItemIdChanged))] private string _itemId = "";

        public string ItemId => _itemId;

        private bool  _picked  = false;
        private float _startLocalY;

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
            EnsureVisualRoot();
            // Guarda posição local Y do visual root para o bobbing
            _startLocalY = _visualRoot != null ? _visualRoot.localPosition.y : 0f;
            RefreshVisual(_itemId);
        }

        private void Awake()
        {
            EnsureVisualRoot();
        }

        /// <summary>
        /// Garante que existe um transform filho para o bobbing.
        /// Se o designer não configurou _visualRoot, cria um GameObject filho
        /// e move os filhos visuais existentes para dentro dele.
        /// </summary>
        private void EnsureVisualRoot()
        {
            if (_visualRoot != null) return;

            // Procura por um filho chamado "VisualRoot"
            var existing = transform.Find("VisualRoot");
            if (existing != null)
            {
                _visualRoot = existing;
                return;
            }

            // Cria o VisualRoot como filho do transform raiz
            var go = new GameObject("VisualRoot");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale    = Vector3.one;
            _visualRoot = go.transform;

            // Move componentes visuais para o VisualRoot
            if (spriteRenderer != null && spriteRenderer.transform == transform)
                spriteRenderer.transform.SetParent(_visualRoot, true);
            if (nameLabel != null && nameLabel.transform == transform)
                nameLabel.transform.SetParent(_visualRoot, true);
            if (glowEffect != null && glowEffect.transform == transform)
                glowEffect.transform.SetParent(_visualRoot, true);
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

        /// <summary>
        /// BUG-08 CORRIGIDO: bobbing aplicado em _visualRoot (filho),
        /// não no transform raiz que o NetworkTransform controla.
        /// Elimina jitter em multiplayer.
        /// </summary>
        private void Update()
        {
            if (!isClient || _visualRoot == null) return;

            float newLocalY = _startLocalY + Mathf.Sin(Time.time * bobFrequency * Mathf.PI * 2f) * bobAmplitude;
            var localPos = _visualRoot.localPosition;
            localPos.y = newLocalY;
            _visualRoot.localPosition = localPos;
        }

        // ── Pickup ─────────────────────────────────────────────────────────

        /// <summary>
        /// BUG-23 CORRIGIDO: usa NetworkServer.spawned[playerNetId] (O(1))
        /// em vez de iterar NetworkPlayer.All (O(n), race condition).
        /// </summary>
        [Command(requiresAuthority = false)]
        public void CmdPickUp(uint playerNetId)
        {
            if (_picked) return;

            // O(1) em vez de O(n) — sem risco de modificação durante iteração
            NetworkPlayer player = null;
            if (NetworkServer.spawned.TryGetValue(playerNetId, out var identity))
                player = identity?.GetComponent<NetworkPlayer>();

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
            // Guard para servidor dedicado (sem UI)
            if (Application.isBatchMode) return;
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
