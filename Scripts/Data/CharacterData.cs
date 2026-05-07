using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG.Data
{
    /// <summary>
    /// CharacterData v3
    ///
    /// CORREÇÕES v3:
    ///   - BaseAttributes não é mais hardcoded como {10,10,10,10,10,10}.
    ///     Agora é passado explicitamente pelo DatabaseManager.
    ///   - AddExperience: loop while com guard de MAX_LEVEL mantido.
    ///   - GetExperienceForLevel: fórmula consistente.
    /// </summary>
    [Serializable]
    public class CharacterData
    {
        public string        CharacterId;
        public string        CharacterName;
        public CharacterRace Race;

        public int RaceInt
        {
            get => (int)Race;
            set => Race = (CharacterRace)value;
        }

        public int           Level                  = 1;
        public long          Experience             = 0;
        public long          ExperienceToNextLevel  = 100;
        public const int     MAX_LEVEL              = 99;

        public BaseAttributes    BaseAttributes   = new BaseAttributes();
        public EquipmentBonuses  EquipmentBonuses = new EquipmentBonuses();

        public float  PosX, PosY, PosZ;
        public string CurrentMap = "World_01";

        public float CurrentHP;
        public float CurrentMP;

        public int FreeAttributePoints = 0;

        public int AllocatedSTR, AllocatedAGI, AllocatedVIT;
        public int AllocatedDEX, AllocatedINT, AllocatedLUK;

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
        /// Adiciona experiência e verifica level up.
        /// Retorna true se houve ao menos um level up.
        /// </summary>
        public bool AddExperience(long amount)
        {
            if (Level >= MAX_LEVEL) return false;

            Experience += amount;
            bool leveled = false;

            while (Experience >= ExperienceToNextLevel && Level < MAX_LEVEL)
            {
                Experience            -= ExperienceToNextLevel;
                Level++;
                FreeAttributePoints   += 5;
                ExperienceToNextLevel  = Level >= MAX_LEVEL ? 0 : GetExperienceForLevel(Level);
                leveled                = true;
            }

            // Garante XP zerado no nível máximo
            if (Level >= MAX_LEVEL)
                Experience = 0;

            return leveled;
        }
    }

    /// <summary>
    /// AccountData — usado apenas para transporte de mensagens de rede.
    /// Characters é carregado separadamente pelo DatabaseManager.
    /// </summary>
    [Serializable]
    public class AccountData
    {
        public string              Username;
        public string              PasswordHash;
        public List<CharacterData> Characters = new List<CharacterData>();
        public string              LastLogin;
    }
}