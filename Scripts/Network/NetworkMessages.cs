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
}
