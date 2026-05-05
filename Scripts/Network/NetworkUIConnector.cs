using UnityEngine;
using Mirror;
using RPG.UI;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// NetworkUIConnector v3 — conecta UIManager ao player local.
    ///
    /// CORREÇÃO v3:
    ///   - Removido NetworkClient.OnSpawnedObject (não existe no Mirror)
    ///   - Mantido fluxo simples e seguro usando TryConnect + retry leve
    /// </summary>
    public class NetworkUIConnector : MonoBehaviour
    {
        private bool _connected;
        private float _retryTimer;

        private void Start()
        {
            TryConnect();
        }

        private void Update()
        {
            if (_connected) return;

            // retry leve (sem custo alto)
            _retryTimer += Time.deltaTime;
            if (_retryTimer >= 0.5f)
            {
                _retryTimer = 0f;
                TryConnect();
            }
        }

        private void TryConnect()
        {
            if (_connected) return;
            if (!NetworkClient.active) return;
            if (NetworkClient.localPlayer == null) return;

            var playerEntity = NetworkClient.localPlayer.GetComponent<PlayerEntity>();
            if (playerEntity == null) return;

            if (playerEntity.IsInitialized)
            {
                BindUI(playerEntity);
            }
            else
            {
                // evita múltiplos binds
                playerEntity.OnInitialized -= OnPlayerInitialized;
                playerEntity.OnInitialized += OnPlayerInitialized;
            }
        }

        private void OnPlayerInitialized()
        {
            if (_connected) return;

            var playerEntity = NetworkClient.localPlayer.GetComponent<PlayerEntity>();
            if (playerEntity != null)
                BindUI(playerEntity);
        }

        private void BindUI(PlayerEntity playerEntity)
        {
            if (_connected) return;
            _connected = true;

            UIManager.Instance?.BindLocalPlayer(playerEntity);
            Debug.Log("[NetworkUIConnector] HUD conectado ao player local.");
        }
    }
}