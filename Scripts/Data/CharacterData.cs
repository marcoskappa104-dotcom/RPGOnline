// ═══════════════════════════════════════════════════════
// CharacterData.cs — ADIÇÃO: campo RaceInt para serialização de rede
// Substitua o arquivo Scripts/Data/CharacterData.cs pelo conteúdo abaixo.
// ═══════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using UnityEngine;
using RPG.Data;

namespace RPG.Data
{
    [Serializable]
    public class CharacterData
    {
        public string        CharacterId;
        public string        CharacterName;
        public CharacterRace Race;

        /// <summary>
        /// Inteiro da raça — necessário para serialização em SyncVars de rede.
        /// Mantido sincronizado com Race.
        /// </summary>
        public int RaceInt
        {
            get => (int)Race;
            set => Race = (CharacterRace)value;
        }

        public int           Level                  = 1;
        public long          Experience             = 0;
        public long          ExperienceToNextLevel  = 100;

        // Atributos base fixos (definidos na criação, nunca mudam)
        public BaseAttributes    BaseAttributes   = new BaseAttributes();
        public EquipmentBonuses  EquipmentBonuses = new EquipmentBonuses();

        // Posição salva
        public float  PosX, PosY, PosZ;
        public string CurrentMap = "World_01";

        // HP/MP persistidos
        public float CurrentHP;
        public float CurrentMP;

        // Pontos de atributo livres
        public int FreeAttributePoints = 0;

        // Atributos alocados pelo jogador
        public int AllocatedSTR, AllocatedAGI, AllocatedVIT;
        public int AllocatedDEX, AllocatedINT, AllocatedLUK;

        /// <summary>
        /// Calcula stats derivados SEM modificar este objeto (sem side-effects).
        /// </summary>
        public DerivedStats GetDerivedStats(BuffBonuses buff = null)
        {
            return StatsCalculator.Calculate(
                BaseAttributes,
                Level,
                Race,
                AllocatedSTR, AllocatedAGI, AllocatedVIT,
                AllocatedDEX, AllocatedINT, AllocatedLUK,
                EquipmentBonuses,
                buff);
        }

        public long GetExperienceForLevel(int level)
        {
            return (long)(100 * Mathf.Pow(level, 1.5f));
        }

        /// <summary>
        /// Adiciona XP e processa level-ups. Retorna true se houve level-up.
        /// Usado APENAS no servidor.
        /// </summary>
        public bool AddExperience(long amount)
        {
            Experience += amount;
            bool leveled = false;
            while (Experience >= ExperienceToNextLevel)
            {
                Experience            -= ExperienceToNextLevel;
                Level++;
                FreeAttributePoints   += 5;
                ExperienceToNextLevel  = GetExperienceForLevel(Level);
                leveled                = true;
            }
            return leveled;
        }
    }

    [Serializable]
    public class AccountData
    {
        public string              Username;
        public string              PasswordHash;
        public List<CharacterData> Characters = new List<CharacterData>();
        public string              LastLogin;
    }
}
