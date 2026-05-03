using UnityEngine;
using Mirror;
using RPG.Data;
using System.Collections.Generic;
using System;

namespace RPG.Network
{
    /// <summary>
    /// ClientAuthHandler — componente cliente de autenticação e seleção de personagem.
    ///
    /// RESPONSABILIDADES:
    ///   - Enviar requests ao servidor: login, criar conta, listar personagens,
    ///     criar personagem, selecionar personagem.
    ///   - Receber respostas e disparar eventos para as UIs.
    ///
    /// COLOQUE NO GAMEOBJECT PERSISTENTE (DontDestroyOnLoad) na cena de Login.
    /// </summary>
    public class ClientAuthHandler : MonoBehaviour
    {
        public static ClientAuthHandler Instance { get; private set; }

        // ── Eventos ────────────────────────────────────────────────────
        public event Action<bool, string>                        OnLoginResult;
        public event Action<bool, string>                        OnCreateAccountResult;
        public event Action<List<CharacterSummary>>              OnCharacterListReceived;
        public event Action<bool, string, List<CharacterSummary>> OnCreateCharacterResult;
        public event Action<bool, string>                        OnSelectCharacterResult;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            NetworkClient.RegisterHandler<MsgLoginResponse>          (OnLoginResponse);
            NetworkClient.RegisterHandler<MsgCreateAccountResponse>  (OnCreateAccountResponse);
            NetworkClient.RegisterHandler<MsgCharacterListResponse>  (OnCharacterListResponse);
            NetworkClient.RegisterHandler<MsgCreateCharacterResponse>(OnCreateCharacterResponse);
            NetworkClient.RegisterHandler<MsgSelectCharacterResponse>(OnSelectCharacterResponse);
        }

        // ── Envio ──────────────────────────────────────────────────────

        public void SendLogin(string username, string password)
        {
            if (!NetworkClient.isConnected)
            { OnLoginResult?.Invoke(false, "Sem conexão com o servidor."); return; }

            NetworkClient.Send(new MsgLoginRequest
            {
                Username     = username.Trim(),
                PasswordHash = Managers.GameManager.HashPassword(password)
            });
        }

        public void SendCreateAccount(string username, string password)
        {
            if (!NetworkClient.isConnected)
            { OnCreateAccountResult?.Invoke(false, "Sem conexão com o servidor."); return; }

            NetworkClient.Send(new MsgCreateAccountRequest
            {
                Username     = username.Trim(),
                PasswordHash = Managers.GameManager.HashPassword(password)
            });
        }

        public void SendRequestCharacterList()
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgRequestCharacterList());
        }

        public void SendCreateCharacter(string name, int raceIndex)
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgCreateCharacterRequest { Name = name.Trim(), RaceIndex = raceIndex });
        }

        public void SendSelectCharacter(string characterId)
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgSelectCharacter { CharacterId = characterId });
        }

        // ── Recebimento ────────────────────────────────────────────────

        private void OnLoginResponse(MsgLoginResponse msg)
        {
            if (msg.Success)
                Managers.GameManager.Instance?.SetLoggedUsername(msg.Username);
            OnLoginResult?.Invoke(msg.Success, msg.Error);
        }

        private void OnCreateAccountResponse(MsgCreateAccountResponse msg)
            => OnCreateAccountResult?.Invoke(msg.Success, msg.Error);

        private void OnCharacterListResponse(MsgCharacterListResponse msg)
            => OnCharacterListReceived?.Invoke(msg.Characters);

        private void OnCreateCharacterResponse(MsgCreateCharacterResponse msg)
            => OnCreateCharacterResult?.Invoke(msg.Success, msg.Error, msg.UpdatedList);

        private void OnSelectCharacterResponse(MsgSelectCharacterResponse msg)
            => OnSelectCharacterResult?.Invoke(msg.Success, msg.Error);
    }
}
