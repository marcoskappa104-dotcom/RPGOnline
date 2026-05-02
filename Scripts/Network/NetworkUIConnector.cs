using UnityEngine;
using Mirror;
using RPG.UI;
using RPG.Character;

namespace RPG.Network
{
    /// <summary>
    /// NetworkUIConnector — conecta o UIManager e o DeathScreenUI ao player local
    /// após o spawn na rede.
    ///
    /// CORREÇÃO: aguarda tanto o PlayerEntity quanto o NetworkPlayer estarem prontos
    /// antes de tentar conectar o HUD, evitando NullReferenceException no startup.
    /// </summary>
    public class NetworkUIConnector : MonoBehaviour
    {
        private bool  _connected;
        private float _retryTimer;
        private const float RETRY_INTERVAL = 0.2f;

        private void Update()
        {
            if (_connected) return;

            _retryTimer += Time.deltaTime;
            if (_retryTimer < RETRY_INTERVAL) return;
            _retryTimer = 0f;

            if (!NetworkClient.active) return;
            if (NetworkClient.localPlayer == null) return;

            var playerEntity = NetworkClient.localPlayer.GetComponent<PlayerEntity>();
            if (playerEntity == null || !playerEntity.IsInitialized) return;

            UIManager.Instance?.BindLocalPlayer(playerEntity);
            _connected = true;

            Debug.Log("[NetworkUIConnector] HUD conectado ao player local.");
        }
    }
}
