using Mirror;
using System.Collections.Generic;

namespace RPG.Network
{
    // ── AUTENTICAÇÃO — Challenge/Response ─────────────────────────────────

    /// <summary>
    /// NOVO v2: Enviado pelo servidor imediatamente após a conexão TCP/UDP.
    /// O cliente deve usar este nonce ao assinar a senha para evitar replay attacks.
    /// </summary>
    public struct MsgAuthChallenge : NetworkMessage
    {
        /// <summary>Nonce único por sessão — Base64(16 bytes aleatórios).</summary>
        public string Nonce;
    }

    // ── LOGIN ──────────────────────────────────────────────────────────────

    public struct MsgLoginRequest : NetworkMessage
    {
        public string Username;
        /// <summary>
        /// SHA-256(SHA-256(senha) + nonce) — o nonce foi recebido via MsgAuthChallenge.
        /// Nunca enviar SHA-256 simples da senha — vulnerável a replay.
        /// </summary>
        public string SignedHash;
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
        /// <summary>
        /// SHA-256 simples da senha (sem nonce) para criação de conta.
        /// O servidor aplicará o salt antes de armazenar.
        /// Em produção, usar TLS + nonce aqui também.
        /// </summary>
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

    // ── ERRO GENÉRICO ──────────────────────────────────────────────────────

    /// <summary>
    /// Resposta genérica de erro para requisições rejeitadas por falta de autenticação
    /// ou outros erros de protocolo.
    /// </summary>
    public struct MsgErrorResponse : NetworkMessage
    {
        public string Error;
    }

    // ── CONFIRMAÇÃO DE CENA ────────────────────────────────────────────────

    /// <summary>
    /// Enviado pelo cliente ao servidor quando a GameplayScene terminou de carregar.
    /// O servidor só então spawna o player, garantindo que o NavMeshAgent funciona.
    /// </summary>
    public struct MsgClientSceneReady : NetworkMessage { }
}