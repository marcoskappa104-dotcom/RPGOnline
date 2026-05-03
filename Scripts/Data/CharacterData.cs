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
        public int           Level                  = 1;
        public long          Experience             = 0;
        public long          ExperienceToNextLevel  = 100;

        // Atributos base (sem raça, sem alocados — apenas o ponto de partida fixo)
        // Estes NUNCA são modificados após a criação do personagem.
        public BaseAttributes    BaseAttributes   = new BaseAttributes();
        public EquipmentBonuses  EquipmentBonuses = new EquipmentBonuses();

        // Posição salva
        public float  PosX, PosY, PosZ;
        public string CurrentMap = "World_01";

        // HP/MP persistidos
        public float CurrentHP;
        public float CurrentMP;

        // Pontos livres para distribuir
        public int FreeAttributePoints = 0;

        // Atributos alocados manualmente pelo jogador
        public int AllocatedSTR, AllocatedAGI, AllocatedVIT;
        public int AllocatedDEX, AllocatedINT, AllocatedLUK;

        /// <summary>
        /// Calcula stats derivados SEM modificar este objeto.
        /// Seguro para chamar múltiplas vezes.
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
        /// Adiciona experiência e processa level-ups.
        /// Retorna true se houve pelo menos um level-up.
        /// </summary>
        public bool AddExperience(long amount)
        {
            Experience += amount;
            bool leveledUp = false;

            while (Experience >= ExperienceToNextLevel)
            {
                Experience            -= ExperienceToNextLevel;
                Level++;
                FreeAttributePoints   += 5;
                ExperienceToNextLevel  = GetExperienceForLevel(Level);
                leveledUp              = true;
            }

            return leveledUp;
        }
    }

    [Serializable]
    public class AccountData
    {
        public string            Username;
        public string            PasswordHash;
        public List<CharacterData> Characters = new List<CharacterData>();
        public string            LastLogin;
    }
}