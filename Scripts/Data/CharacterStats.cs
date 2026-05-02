using System;
using UnityEngine;

namespace RPG.Data
{
    [Serializable]
    public enum CharacterRace
    {
        Human,
        Elf,
        Dwarf,
        Orc,
        Undead
    }

    [Serializable]
    public class BaseAttributes
    {
        public int STR = 10; // Força
        public int AGI = 10; // Agilidade
        public int VIT = 10; // Vitalidade
        public int DEX = 10; // Destreza
        public int INT = 10; // Inteligência
        public int LUK = 10; // Sorte
    }

    [Serializable]
    public class RaceBonus
    {
        public int STR, AGI, VIT, DEX, INT, LUK;
    }

    [Serializable]
    public class DerivedStats
    {
        // Principais
        public float MaxHP;
        public float CurrentHP;
        public float MaxMP;
        public float CurrentMP;
        public float ATK;
        public float MATK;
        public float DEF;
        public float MDEF;

        // Combate
        public float ASPD;
        public float HIT;
        public float FLEE;
        public float CRIT;
        public float CritDMG;

        // Regen
        public float HPRegen;
        public float MPRegen;
        public float CastSpeed;

        // Avançados
        public float Penetration;
        public float DamageReduction;

        // Resistências (0-100%)
        public float ResistFire;
        public float ResistIce;
        public float ResistPoison;
        public float ResistLightning;
    }

    [Serializable]
    public class EquipmentBonuses
    {
        public int STR, AGI, VIT, DEX, INT, LUK;
        public float ATK, DEF, MATK, MDEF;
        public float HPBonus, MPBonus;
    }

    [Serializable]
    public class BuffBonuses
    {
        public int STR, AGI, VIT, DEX, INT, LUK;
        public float ATKMultiplier = 1f;
        public float DEFMultiplier = 1f;
    }

    public static class StatsCalculator
    {
        // Base values per race
        public static readonly int BASE_HP = 100;
        public static readonly int BASE_MP = 50;
        public static readonly float BASE_ASPD = 1.0f;

        public static RaceBonus GetRaceBonus(CharacterRace race)
        {
            return race switch
            {
                CharacterRace.Human  => new RaceBonus { STR=2,  AGI=2,  VIT=2,  DEX=2,  INT=2,  LUK=5  },
                CharacterRace.Elf    => new RaceBonus { STR=0,  AGI=5,  VIT=0,  DEX=5,  INT=5,  LUK=3  },
                CharacterRace.Dwarf  => new RaceBonus { STR=5,  AGI=0,  VIT=8,  DEX=2,  INT=0,  LUK=2  },
                CharacterRace.Orc    => new RaceBonus { STR=8,  AGI=2,  VIT=5,  DEX=0,  INT=0,  LUK=0  },
                CharacterRace.Undead => new RaceBonus { STR=2,  AGI=2,  VIT=0,  DEX=2,  INT=8,  LUK=0  },
                _ => new RaceBonus()
            };
        }

        /// <summary>
        /// Calcula todos os status derivados com base nos atributos + equip + buff
        /// FinalStat = (Base + Equip + Buff) * Multiplicadores
        /// </summary>
        public static DerivedStats Calculate(BaseAttributes baseAttr, int level,
            EquipmentBonuses equip = null, BuffBonuses buff = null)
        {
            equip ??= new EquipmentBonuses();
            buff  ??= new BuffBonuses();

            // Atributos finais
            float STR = (baseAttr.STR + equip.STR + buff.STR);
            float AGI = (baseAttr.AGI + equip.AGI + buff.AGI);
            float VIT = (baseAttr.VIT + equip.VIT + buff.VIT);
            float DEX = (baseAttr.DEX + equip.DEX + buff.DEX);
            float INT = (baseAttr.INT + equip.INT + buff.INT);
            float LUK = (baseAttr.LUK + equip.LUK + buff.LUK);

            var s = new DerivedStats();

            // HP & MP
            s.MaxHP  = BASE_HP + (VIT * 50f) + (STR * 10f) + equip.HPBonus;
            s.MaxMP  = BASE_MP + (INT * 40f) + (DEX * 5f)  + equip.MPBonus;

            // ATK & MATK com multiplicadores de buff e equip
            s.ATK  = ((STR * 2f) + (DEX * 1f) + level + equip.ATK) * buff.ATKMultiplier;
            s.MATK = ((INT * 2.5f) + (DEX * 0.5f) + level + equip.MATK) * buff.ATKMultiplier;

            // DEF & MDEF
            s.DEF  = ((VIT * 2f) + (STR * 0.5f) + equip.DEF) * buff.DEFMultiplier;
            s.MDEF = ((INT * 2f) + (VIT * 1f) + equip.MDEF)  * buff.DEFMultiplier;

            // Velocidades
            s.ASPD = BASE_ASPD + (AGI * 0.5f) + (DEX * 0.2f);

            // Precisão e esquiva
            s.HIT  = (DEX * 2f) + (LUK * 0.5f);
            s.FLEE = (AGI * 2f) + (LUK * 0.3f);

            // Crítico
            s.CRIT    = LUK * 0.3f;   // em %
            s.CritDMG = 1.5f;          // multiplicador base (pode subir com itens)

            // Regen
            s.HPRegen  = (VIT * 0.5f) + (level * 0.2f);
            s.MPRegen  = (INT * 0.5f) + (level * 0.2f);

            // Cast speed
            s.CastSpeed = (DEX * 0.5f) + (INT * 0.3f);

            // Avançados
            s.Penetration    = STR * 0.2f;
            s.DamageReduction = VIT * 0.1f;

            return s;
        }

        // ─── Fórmulas de dano ────────────────────────────────────────────────

        public static float CalculatePhysicalDamage(float atk, float def, bool isCrit, float critDmgMult = 1.5f)
        {
            float reduction = def / (def + 100f);
            float raw = atk * (1f - reduction);
            raw = Mathf.Max(1f, raw); // dano mínimo 1
            if (isCrit) raw *= critDmgMult;
            return Mathf.Floor(raw);
        }

        public static float CalculateMagicDamage(float matk, float mdef, bool isCrit, float critDmgMult = 1.5f)
        {
            float reduction = mdef / (mdef + 100f);
            float raw = matk * (1f - reduction);
            raw = Mathf.Max(1f, raw);
            if (isCrit) raw *= critDmgMult;
            return Mathf.Floor(raw);
        }

        public static bool RollCrit(float critChance)
        {
            return UnityEngine.Random.Range(0f, 100f) < critChance;
        }

        public static bool RollHit(float hit, float flee)
        {
            float hitChance = Mathf.Clamp(hit / (hit + flee) * 100f, 5f, 95f);
            return UnityEngine.Random.Range(0f, 100f) < hitChance;
        }
    }
}
