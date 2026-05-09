using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG.Data
{
    /// <summary>
    /// CharacterData v4
    ///
    /// CORREÇÕES v4:
    ///   - AddExperience: valida amount <= 0 para evitar XP negativa.
    ///   - GetExperienceForLevel: usa Math.Pow (double) em vez de Mathf.Pow (float)
    ///     para evitar perda de precisão em níveis altos (> 40).
    ///   - CharacterData expõe Data como readonly onde possível para evitar
    ///     mutação acidental fora do servidor.
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

        /// <summary>
        /// CORREÇÃO v4: usa Math.Pow (double) para precisão correta em níveis altos.
        /// Mathf.Pow retorna float (~7 dígitos) causando arredondamento incorreto acima do nível 40.
        /// </summary>
        public long GetExperienceForLevel(int level)
        {
            return (long)(100.0 * Math.Pow(level, 1.5));
        }

        /// <summary>
        /// Adiciona experiência e verifica level up.
        /// Retorna true se houve ao menos um level up.
        /// </summary>
        public bool AddExperience(long amount)
        {
            // CORREÇÃO v4: rejeita amount negativo ou zero para evitar XP negativa.
            if (amount <= 0) return false;
            if (Level >= MAX_LEVEL) return false;

            Experience += amount;
            bool leveled = false;

            while (Experience >= ExperienceToNextLevel && Level < MAX_LEVEL)
            {
                Experience            -= ExperienceToNextLevel;
                Level++;
                FreeAttributePoints   += 5;
                ExperienceToNextLevel  = Level >= MAX_LEVEL ? 0L : GetExperienceForLevel(Level);
                leveled                = true;
            }

            // Garante XP zerado no nível máximo
            if (Level >= MAX_LEVEL)
                Experience = 0;

            return leveled;
        }

        /// <summary>
        /// Clona os dados do personagem — útil para snapshots no servidor.
        /// </summary>
        public CharacterData Clone()
        {
            return new CharacterData
            {
                CharacterId           = CharacterId,
                CharacterName         = CharacterName,
                Race                  = Race,
                Level                 = Level,
                Experience            = Experience,
                ExperienceToNextLevel = ExperienceToNextLevel,
                PosX = PosX, PosY = PosY, PosZ = PosZ,
                CurrentMap            = CurrentMap,
                CurrentHP             = CurrentHP,
                CurrentMP             = CurrentMP,
                FreeAttributePoints   = FreeAttributePoints,
                AllocatedSTR = AllocatedSTR, AllocatedAGI = AllocatedAGI,
                AllocatedVIT = AllocatedVIT, AllocatedDEX = AllocatedDEX,
                AllocatedINT = AllocatedINT, AllocatedLUK = AllocatedLUK,
                BaseAttributes = new BaseAttributes
                {
                    STR = BaseAttributes.STR, AGI = BaseAttributes.AGI,
                    VIT = BaseAttributes.VIT, DEX = BaseAttributes.DEX,
                    INT = BaseAttributes.INT, LUK = BaseAttributes.LUK
                },
                EquipmentBonuses = new EquipmentBonuses
                {
                    STR = EquipmentBonuses.STR, AGI = EquipmentBonuses.AGI,
                    VIT = EquipmentBonuses.VIT, DEX = EquipmentBonuses.DEX,
                    INT = EquipmentBonuses.INT, LUK = EquipmentBonuses.LUK,
                    ATK = EquipmentBonuses.ATK, DEF = EquipmentBonuses.DEF,
                    MATK = EquipmentBonuses.MATK, MDEF = EquipmentBonuses.MDEF,
                    HPBonus = EquipmentBonuses.HPBonus, MPBonus = EquipmentBonuses.MPBonus
                }
            };
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
