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
        public int STR = 10;
        public int AGI = 10;
        public int VIT = 10;
        public int DEX = 10;
        public int INT = 10;
        public int LUK = 10;
    }

    [Serializable]
    public class RaceBonus
    {
        public int STR, AGI, VIT, DEX, INT, LUK;
    }

    /// <summary>
    /// DerivedStats v2
    /// CORREÇÃO: classe → struct-like imutável via cópia.
    /// Mantida como classe por compatibilidade Mirror/SyncVar,
    /// mas métodos de mutação direta (Stats.MaxHP = x) foram substituídos
    /// por SetMaxHP/SetMaxMP no PlayerEntity para controle centralizado.
    /// </summary>
    [Serializable]
    public class DerivedStats
    {
        // Vitais
        public float MaxHP;
        public float MaxMP;

        // Combate
        public float ATK;
        public float MATK;
        public float DEF;
        public float MDEF;

        // Velocidade
        /// <summary>Ataques por segundo.</summary>
        public float ASPD;
        /// <summary>Velocidade de deslocamento em m/s para o NavMeshAgent.</summary>
        public float MoveSpeed;
        public float CastSpeed;

        // Precisão
        public float HIT;
        public float FLEE;
        public float CRIT;
        public float CritDMG;

        // Regen por tick (ServerRegenLoop — intervalo de 5s)
        public float HPRegen;
        public float MPRegen;

        // Avançados
        public float Penetration;
        public float DamageReduction;

        // Resistências (0–100%) — agora populadas pelo StatsCalculator
        public float ResistFire;
        public float ResistIce;
        public float ResistPoison;
        public float ResistLightning;

        /// <summary>Cria uma cópia rasa — evita mutações acidentais do objeto original.</summary>
        public DerivedStats Clone() => (DerivedStats)MemberwiseClone();
    }

    [Serializable]
    public class EquipmentBonuses
    {
        public int   STR, AGI, VIT, DEX, INT, LUK;
        public float ATK, DEF, MATK, MDEF;
        public float HPBonus, MPBonus;
        // Resistências via equipamento
        public float ResistFire, ResistIce, ResistPoison, ResistLightning;
    }

    [Serializable]
    public class BuffBonuses
    {
        public int   STR, AGI, VIT, DEX, INT, LUK;
        public float ATKMultiplier = 1f;
        public float DEFMultiplier = 1f;
    }

    public static class StatsCalculator
    {
        public const int   BASE_HP        = 100;
        public const int   BASE_MP        = 50;
        public const float BASE_ASPD      = 0.5f;
        public const float BASE_MOVESPEED = 4.0f;

        public static RaceBonus GetRaceBonus(CharacterRace race)
        {
            return race switch
            {
                CharacterRace.Human  => new RaceBonus { STR=2, AGI=2, VIT=2, DEX=2, INT=2, LUK=5 },
                CharacterRace.Elf    => new RaceBonus { STR=0, AGI=5, VIT=0, DEX=5, INT=5, LUK=3 },
                CharacterRace.Dwarf  => new RaceBonus { STR=5, AGI=0, VIT=8, DEX=2, INT=0, LUK=2 },
                CharacterRace.Orc    => new RaceBonus { STR=8, AGI=2, VIT=5, DEX=0, INT=0, LUK=0 },
                CharacterRace.Undead => new RaceBonus { STR=2, AGI=2, VIT=0, DEX=2, INT=8, LUK=0 },
                _                    => new RaceBonus()
            };
        }

        /// <summary>
        /// Calcula os stats derivados. Sem side-effects nos objetos passados.
        /// THREAD-SAFE: não usa UnityEngine.Random (apenas aritmética pura).
        /// </summary>
        public static DerivedStats Calculate(
            BaseAttributes   baseAttr,
            int              level,
            CharacterRace    race,
            int              allocSTR  = 0,
            int              allocAGI  = 0,
            int              allocVIT  = 0,
            int              allocDEX  = 0,
            int              allocINT  = 0,
            int              allocLUK  = 0,
            EquipmentBonuses equip     = null,
            BuffBonuses      buff      = null)
        {
            equip ??= new EquipmentBonuses();
            buff  ??= new BuffBonuses();

            var raceBonus = GetRaceBonus(race);

            // Garante que atributos alocados não sejam negativos
            allocSTR = Math.Max(0, allocSTR);
            allocAGI = Math.Max(0, allocAGI);
            allocVIT = Math.Max(0, allocVIT);
            allocDEX = Math.Max(0, allocDEX);
            allocINT = Math.Max(0, allocINT);
            allocLUK = Math.Max(0, allocLUK);

            float STR = baseAttr.STR + raceBonus.STR + allocSTR + equip.STR + buff.STR;
            float AGI = baseAttr.AGI + raceBonus.AGI + allocAGI + equip.AGI + buff.AGI;
            float VIT = baseAttr.VIT + raceBonus.VIT + allocVIT + equip.VIT + buff.VIT;
            float DEX = baseAttr.DEX + raceBonus.DEX + allocDEX + equip.DEX + buff.DEX;
            float INT = baseAttr.INT + raceBonus.INT + allocINT + equip.INT + buff.INT;
            float LUK = baseAttr.LUK + raceBonus.LUK + allocLUK + equip.LUK + buff.LUK;

            // Garante mínimo de 1 em cada atributo
            STR = Math.Max(1f, STR);
            AGI = Math.Max(1f, AGI);
            VIT = Math.Max(1f, VIT);
            DEX = Math.Max(1f, DEX);
            INT = Math.Max(1f, INT);
            LUK = Math.Max(1f, LUK);

            var s = new DerivedStats();

            s.MaxHP = BASE_HP + (VIT * 20f) + (STR * 5f) + (level * 10f) + equip.HPBonus;
            s.MaxMP = BASE_MP + (INT * 15f) + (DEX * 3f) + (level * 5f)  + equip.MPBonus;

            // Garante mínimo de 1 em HP/MP
            s.MaxHP = Math.Max(1f, s.MaxHP);
            s.MaxMP = Math.Max(1f, s.MaxMP);

            s.ATK  = ((STR * 1.5f) + (DEX * 0.5f) + level + equip.ATK)  * buff.ATKMultiplier;
            s.MATK = ((INT * 2.0f) + (DEX * 0.5f) + level + equip.MATK) * buff.ATKMultiplier;

            s.DEF  = ((VIT * 1.2f) + (STR * 0.3f) + equip.DEF)  * buff.DEFMultiplier;
            s.MDEF = ((INT * 1.2f) + (VIT * 0.5f) + equip.MDEF) * buff.DEFMultiplier;

            s.ASPD      = Mathf.Clamp(BASE_ASPD + (AGI * 0.02f) + (DEX * 0.01f), 0.3f, 3.0f);
            s.MoveSpeed = Mathf.Clamp(BASE_MOVESPEED + (AGI * 0.03f), 3f, 7f);

            s.HIT  = (DEX * 2f) + (LUK * 0.5f);
            s.FLEE = (AGI * 2f) + (LUK * 0.3f);

            s.CRIT    = LUK * 0.3f;
            s.CritDMG = 1.5f;

            // HP/MP regen por tick de 5s
            s.HPRegen  = (VIT * 0.5f) + (level * 0.2f);
            s.MPRegen  = (INT * 0.5f) + (level * 0.2f);

            s.CastSpeed = (DEX * 0.5f) + (INT * 0.3f);

            s.Penetration     = STR * 0.2f;
            s.DamageReduction = VIT * 0.1f;

            // Resistências via equipamento (clampadas 0–75% para evitar imunidade total)
            s.ResistFire      = Mathf.Clamp(equip.ResistFire,      0f, 75f);
            s.ResistIce       = Mathf.Clamp(equip.ResistIce,       0f, 75f);
            s.ResistPoison    = Mathf.Clamp(equip.ResistPoison,    0f, 75f);
            s.ResistLightning = Mathf.Clamp(equip.ResistLightning, 0f, 75f);

            return s;
        }

        // ── Fórmulas de dano ──────────────────────────────────────────────

        public static float CalculatePhysicalDamage(
            float atk, float def, bool isCrit, float critDmgMult = 1.5f)
        {
            float reduction = def / (def + 100f);
            float raw       = atk * (1f - reduction);
            raw             = Math.Max(1f, raw);
            if (isCrit) raw *= critDmgMult;
            return Mathf.Floor(raw);
        }

        public static float CalculateMagicDamage(
            float matk, float mdef, bool isCrit, float critDmgMult = 1.5f)
        {
            float reduction = mdef / (mdef + 100f);
            float raw       = matk * (1f - reduction);
            raw             = Math.Max(1f, raw);
            if (isCrit) raw *= critDmgMult;
            return Mathf.Floor(raw);
        }

        /// <summary>
        /// THREAD-SAFE: aceita um System.Random passado pelo chamador.
        /// Para uso no servidor: passe um System.Random instanciado lá.
        /// Para uso no cliente (Unity main thread): passe null para usar UnityEngine.Random.
        /// </summary>
        public static bool RollCrit(float critChance, System.Random rng = null)
        {
            float roll = rng != null
                ? (float)(rng.NextDouble() * 100.0)
                : UnityEngine.Random.Range(0f, 100f);
            return roll < critChance;
        }

        /// <summary>THREAD-SAFE: mesma lógica de RollCrit.</summary>
        public static bool RollHit(float hit, float flee, System.Random rng = null)
        {
            float hitChance = Mathf.Clamp(hit / (hit + flee) * 100f, 5f, 95f);
            float roll = rng != null
                ? (float)(rng.NextDouble() * 100.0)
                : UnityEngine.Random.Range(0f, 100f);
            return roll < hitChance;
        }
    }
}
