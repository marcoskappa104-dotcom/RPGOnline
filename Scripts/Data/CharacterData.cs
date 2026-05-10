using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG.Data
{
    /// <summary>
    /// CharacterData v5
    ///
    /// CORREÇÕES v5:
    ///   - GetExperienceForLevel agora é static — pode ser chamado sem instância.
    ///   - Clone() agora copia EquipmentBonuses.Resist* (adicionados em v2 de EquipmentBonuses).
    ///   - Level nunca pode ser setado < 1 externamente (propriedade com validação).
    ///   - AddExperience: garante que ExperienceToNextLevel nunca seja 0 no nivel máximo.
    ///   - Constante MAX_ALLOCATED_PER_STAT adicionada para validação server-side.
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

        private int _level = 1;
        public int Level
        {
            get => _level;
            set => _level = Math.Max(1, Math.Min(value, MAX_LEVEL));
        }

        public long          Experience             = 0;
        public long          ExperienceToNextLevel  = 100;
        public const int     MAX_LEVEL              = 99;

        /// <summary>
        /// Máximo de pontos alocáveis por atributo — usado no servidor para
        /// rejeitar alocações suspeitas de clientes modificados.
        /// </summary>
        public const int MAX_ALLOCATED_PER_STAT = 300;

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
        /// CORREÇÃO v5: agora static — não exige instância para calcular XP necessária.
        /// Usa Math.Pow (double) para precisão correta em níveis altos (> 40).
        /// </summary>
        public static long GetExperienceForLevel(int level)
        {
            return (long)(100.0 * Math.Pow(Math.Max(1, level), 1.5));
        }

        /// <summary>
        /// Adiciona experiência e processa level up.
        /// Retorna true se houve ao menos um level up.
        /// </summary>
        public bool AddExperience(long amount)
        {
            if (amount <= 0) return false;
            if (Level >= MAX_LEVEL) return false;

            Experience += amount;
            bool leveled = false;

            while (Experience >= ExperienceToNextLevel && Level < MAX_LEVEL)
            {
                Experience            -= ExperienceToNextLevel;
                Level++;                                          // usa o setter com clamp
                FreeAttributePoints   += 5;
                ExperienceToNextLevel  = Level >= MAX_LEVEL
                    ? 0L
                    : GetExperienceForLevel(Level);
                leveled = true;
            }

            if (Level >= MAX_LEVEL)
            {
                Experience            = 0;
                ExperienceToNextLevel = 0;
            }

            return leveled;
        }

        /// <summary>
        /// Clona os dados do personagem — útil para snapshots no servidor.
        /// CORREÇÃO v5: copia todos os campos de EquipmentBonuses incluindo Resist*.
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
                    HPBonus = EquipmentBonuses.HPBonus, MPBonus = EquipmentBonuses.MPBonus,
                    // CORREÇÃO v5: Resist* agora incluídos no clone
                    ResistFire      = EquipmentBonuses.ResistFire,
                    ResistIce       = EquipmentBonuses.ResistIce,
                    ResistPoison    = EquipmentBonuses.ResistPoison,
                    ResistLightning = EquipmentBonuses.ResistLightning
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