using Mirror;
using System.Collections.Generic;

namespace RPG.Network
{
    // ── LOGIN ──────────────────────────────────────────────────────────────

    public struct MsgLoginRequest : NetworkMessage
    {
        public string Username;
        public string PasswordHash; // SHA-256 feito no cliente antes de enviar
    }

    public struct MsgLoginResponse : NetworkMessage
    {
        public bool   Success;
        public string Error;       // mensagem de erro se !Success
        public string Username;    // confirmado pelo servidor
    }

    // ── CRIAR CONTA ────────────────────────────────────────────────────────

    public struct MsgCreateAccountRequest : NetworkMessage
    {
        public string Username;
        public string PasswordHash;
    }

    public struct MsgCreateAccountResponse : NetworkMessage
    {
        public bool   Success;
        public string Error;
    }

    // ── LISTA DE PERSONAGENS ───────────────────────────────────────────────

    public struct MsgRequestCharacterList : NetworkMessage { }

    public struct CharacterSummary : NetworkMessage
    {
        public string CharacterId;
        public string CharacterName;
        public string Race;
        public int    Level;
    }

    public struct MsgCharacterListResponse : NetworkMessage
    {
        public List<CharacterSummary> Characters;
    }

    // ── CRIAR PERSONAGEM ───────────────────────────────────────────────────

    public struct MsgCreateCharacterRequest : NetworkMessage
    {
        public string Name;
        public int    RaceIndex; // índice do enum CharacterRace
    }

    public struct MsgCreateCharacterResponse : NetworkMessage
    {
        public bool   Success;
        public string Error;
        public List<CharacterSummary> UpdatedList; // lista atualizada após criação
    }

    // ── SELECIONAR PERSONAGEM / ENTRAR NO JOGO ─────────────────────────────

    public struct MsgSelectCharacter : NetworkMessage
    {
        public string CharacterId;
    }

    public struct MsgSelectCharacterResponse : NetworkMessage
    {
        public bool   Success;
        public string Error;
    }
	// ── ERRO GENÉRICO ──────────────────────────────────────────────────────────

/// <summary>
/// Resposta genérica de erro para requisições rejeitadas por falta de autenticação
/// ou outros erros de protocolo. Evita enviar MsgLoginResponse em contextos errados.
/// </summary>
public struct MsgErrorResponse : NetworkMessage
{
    public string Error;
}

// ── CONFIRMAÇÃO DE CENA ────────────────────────────────────────────────────

/// <summary>
/// Enviado pelo cliente ao servidor quando a GameplayScene terminou de carregar.
/// O servidor só então spawna o player, garantindo que o NavMeshAgent funciona.
/// Movido de RPGNetworkManager.cs para cá para centralizar todas as mensagens.
/// </summary>
public struct MsgClientSceneReady : NetworkMessage { }
}
