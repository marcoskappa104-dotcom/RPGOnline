using System;
using System.Collections.Generic;
using UnityEngine;
using RPG.Data;

namespace RPG.Data
{
    [Serializable]
    public class CharacterData
    {
        public string CharacterId;
        public string CharacterName;
        public CharacterRace Race;
        public int Level = 1;
        public long Experience = 0;
        public long ExperienceToNextLevel = 100;

        public BaseAttributes BaseAttributes = new BaseAttributes();
        public EquipmentBonuses EquipmentBonuses = new EquipmentBonuses();

        // Posição salva no mundo
        public float PosX, PosY, PosZ;
        public string CurrentMap = "World_01";

        // HP/MP atuais (persistidos)
        public float CurrentHP;
        public float CurrentMP;

        // Pontos de atributo disponíveis para distribuir
        public int FreeAttributePoints = 0;

        // Número de atributo base alocados manualmente
        public int AllocatedSTR, AllocatedAGI, AllocatedVIT, AllocatedDEX, AllocatedINT, AllocatedLUK;

        public void ApplyRaceBonus()
        {
            var bonus = StatsCalculator.GetRaceBonus(Race);
            BaseAttributes.STR = 10 + bonus.STR + AllocatedSTR;
            BaseAttributes.AGI = 10 + bonus.AGI + AllocatedAGI;
            BaseAttributes.VIT = 10 + bonus.VIT + AllocatedVIT;
            BaseAttributes.DEX = 10 + bonus.DEX + AllocatedDEX;
            BaseAttributes.INT = 10 + bonus.INT + AllocatedINT;
            BaseAttributes.LUK = 10 + bonus.LUK + AllocatedLUK;
        }

        public DerivedStats GetDerivedStats(BuffBonuses buff = null)
        {
            ApplyRaceBonus();
            return StatsCalculator.Calculate(BaseAttributes, Level, EquipmentBonuses, buff);
        }

        public long GetExperienceForLevel(int level)
        {
            // Fórmula simples: 100 * level^1.5
            return (long)(100 * Mathf.Pow(level, 1.5f));
        }

        public bool AddExperience(long amount)
        {
            Experience += amount;
            bool leveledUp = false;
            while (Experience >= ExperienceToNextLevel)
            {
                Experience -= ExperienceToNextLevel;
                Level++;
                FreeAttributePoints += 5;
                ExperienceToNextLevel = GetExperienceForLevel(Level);
                leveledUp = true;
            }
            return leveledUp;
        }
    }

    [Serializable]
    public class AccountData
    {
        public string Username;
        public string PasswordHash; // nunca armazenamos senha pura
        public List<CharacterData> Characters = new List<CharacterData>();
        public string LastLogin;
    }
}
