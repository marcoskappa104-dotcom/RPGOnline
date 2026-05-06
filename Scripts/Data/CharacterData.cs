using System;
using System.Collections.Generic;
using UnityEngine;
using RPG.Data;

namespace RPG.Data
{
    /// <summary>
    /// CharacterData v2 — sem alterações de lógica, apenas documentação atualizada.
    /// Os dados agora vêm do SQLite (DatabaseManager) e não de JSON.
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
		public const int MAX_LEVEL = 99;
		
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


public bool AddExperience(long amount)
{
    if (Level >= MAX_LEVEL) return false; // já no nível máximo

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

    // Garante que XP não ultrapasse o necessário no nível máximo
    if (Level >= MAX_LEVEL)
        Experience = 0;

    return leveled;
}
    }

    /// <summary>
    /// AccountData v2 — simplificado.
    /// Characters agora é carregado separadamente pelo DatabaseManager.
    /// Mantemos a lista para compatibilidade com mensagens de rede (CharacterListResponse).
    /// </summary>
    [Serializable]
    public class AccountData
    {
        public string              Username;
        public string              PasswordHash;
        public List<CharacterData> Characters = new List<CharacterData>(); // populado pelo DatabaseManager.TryLoginWithHash
        public string              LastLogin;
    }
}
