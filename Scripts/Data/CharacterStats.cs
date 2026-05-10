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

    // Raça interna para monstros — sem bônus de atributo de raça jogável
    internal enum MonsterRaceInternal { Monster }

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
    /// DerivedStats v3
    ///
    /// CORREÇÕES v3:
    ///   - CastSpeed agora é USADO pelo sistema de skills (reduz CastTime da SkillData).
    ///   - Penetration agora é USADO por CalculatePhysicalDamage/CalculateMagicDamage.
    ///   - DamageReduction agora é USADO por CalculatePhysicalDamage.
    ///   - CritDMG agora escala com LUK em vez de ser fixo em 1.5x.
    ///   - RollHit protegido contra NaN quando hit e flee são 0.
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
        /// <summary>
        /// Velocidade de cast. Reduz o CastTime da SkillData:
        ///   effectiveCastTime = skill.CastTime / (1f + CastSpeed / 100f)
        /// Gerado por DEX e INT.
        /// </summary>
        public float CastSpeed;

        // Precisão
        public float HIT;
        public float FLEE;
        public float CRIT;
        /// <summary>
        /// Multiplicador de dano crítico. Escala com LUK.
        /// Mínimo 1.5x, máximo 3.0x.
        /// </summary>
        public float CritDMG;

        // Regen por tick (ServerRegenLoop — intervalo de 5s)
        public float HPRegen;
        public float MPRegen;

        /// <summary>
        /// Penetração de defesa física (flat). Reduz a DEF efetiva do alvo antes do cálculo.
        /// Gerado por STR. Ex: 10 Penetração contra 50 DEF → calcula como se fosse 40 DEF.
        /// </summary>
        public float Penetration;

        /// <summary>
        /// Penetração de resistência mágica (flat). Reduz a MDEF efetiva do alvo.
        /// Gerado por INT.
        /// </summary>
        public float MagicPenetration;

        /// <summary>
        /// Redução de dano flat antes do cálculo percentual de DEF.
        /// Gerado por VIT. Ex: 5 DamageReduction → reduz dano recebido em 5 antes da DEF.
        /// </summary>
        public float DamageReduction;

        // Resistências (0–75%) — aplicadas a dano elemental
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
        // ── Constantes base ───────────────────────────────────────────────
        // REBALANCEAMENTO v3:
        //   - BASE_HP reduzido de 100 para 50 (HP base menor, VIT mais relevante)
        //   - BASE_ASPD aumentado de 0.5 para 0.8 (combate mais fluido no início)
        //   - HP_PER_VIT aumentado de 20 para 15 (menos inflação de HP)
        //   - HP_PER_LEVEL aumentado de 10 para 8
        //   - MP_PER_INT mantido em 15
        //   - ATK_PER_STR reduzido de 1.5 para 1.2 (dano mais suave)
        //   - MATK_PER_INT reduzido de 2.0 para 1.5
        //   - DEF_PER_VIT reduzido de 1.2 para 1.0
        //   - MDEF_PER_INT reduzido de 1.2 para 1.0
        public const int   BASE_HP         = 50;
        public const int   BASE_MP         = 30;
        public const float BASE_ASPD       = 0.8f;   // ataques/segundo (era 0.5)
        public const float BASE_MOVESPEED  = 4.0f;
        public const float MAX_ASPD        = 4.0f;   // cap em 4 ataques/segundo
        public const float MIN_ASPD        = 0.3f;   // mínimo 0.3 ataques/segundo
        public const float MAX_MOVESPEED   = 7.5f;
        public const float MIN_MOVESPEED   = 3.0f;
        public const float MIN_CRIT_DMG    = 1.5f;
        public const float MAX_CRIT_DMG    = 3.0f;
        public const float MAX_PENETRATION = 200f;   // cap de penetração física
        public const float MAX_RESIST      = 75f;    // cap de resistência elemental

        // ── Coeficientes de atributo ───────────────────────────────────────
        private const float HP_PER_VIT      = 15f;
        private const float HP_PER_STR      = 3f;
        private const float HP_PER_LEVEL    = 8f;
        private const float MP_PER_INT      = 12f;
        private const float MP_PER_DEX      = 2f;
        private const float MP_PER_LEVEL    = 4f;
        private const float ATK_PER_STR     = 1.2f;
        private const float ATK_PER_DEX     = 0.4f;
        private const float MATK_PER_INT    = 1.5f;
        private const float MATK_PER_DEX    = 0.4f;
        private const float DEF_PER_VIT     = 1.0f;
        private const float DEF_PER_STR     = 0.2f;
        private const float MDEF_PER_INT    = 1.0f;
        private const float MDEF_PER_VIT    = 0.3f;
        private const float ASPD_PER_AGI    = 0.015f;
        private const float ASPD_PER_DEX    = 0.008f;
        private const float MOVE_PER_AGI    = 0.025f;
        private const float HIT_PER_DEX     = 2.0f;
        private const float HIT_PER_LUK     = 0.3f;
        private const float FLEE_PER_AGI    = 2.0f;
        private const float FLEE_PER_LUK    = 0.2f;
        private const float CRIT_PER_LUK    = 0.25f;
        private const float CRITDMG_PER_LUK = 0.01f; // +1% CritDMG por LUK acima de 50
        private const float HPREGEN_PER_VIT  = 0.8f;
        private const float HPREGEN_PER_LVL  = 0.3f;
        private const float MPREGEN_PER_INT  = 0.6f;
        private const float MPREGEN_PER_LVL  = 0.2f;
        private const float CAST_PER_DEX    = 0.4f;
        private const float CAST_PER_INT    = 0.3f;
        private const float PEN_PER_STR     = 0.15f;
        private const float MPEN_PER_INT    = 0.12f;
        private const float DMGRED_PER_VIT  = 0.08f;

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
        /// Calcula stats derivados para JOGADORES.
        /// Sem side-effects nos objetos passados. THREAD-SAFE.
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

            return CalculateInternal(STR, AGI, VIT, DEX, INT, LUK, level, equip, buff);
        }

        /// <summary>
        /// Calcula stats derivados para MONSTROS, sem bônus de raça.
        /// Suporta escala por nível via multiplicador.
        /// </summary>
        public static DerivedStats CalculateForMonster(
            BaseAttributes baseAttr,
            int            level)
        {
            // Monstros escalam seus stats base com o nível
            // Multiplicador: 1.0 no nível 1, ~3.5 no nível 99
            float levelMult = 1f + (level - 1) * 0.025f; // +2.5% por nível acima de 1

            float STR = Math.Max(1f, baseAttr.STR * levelMult);
            float AGI = Math.Max(1f, baseAttr.AGI * levelMult);
            float VIT = Math.Max(1f, baseAttr.VIT * levelMult);
            float DEX = Math.Max(1f, baseAttr.DEX * levelMult);
            float INT = Math.Max(1f, baseAttr.INT * levelMult);
            float LUK = Math.Max(1f, baseAttr.LUK * levelMult);

            // Monstros não têm equipamentos nem buffs de personagem
            return CalculateInternal(STR, AGI, VIT, DEX, INT, LUK, level,
                                     new EquipmentBonuses(), new BuffBonuses());
        }

        /// <summary>
        /// Núcleo de cálculo. Recebe atributos totais já somados.
        /// Usado tanto por jogadores quanto por monstros.
        /// </summary>
        private static DerivedStats CalculateInternal(
            float            STR, float AGI, float VIT,
            float            DEX, float INT, float LUK,
            int              level,
            EquipmentBonuses equip,
            BuffBonuses      buff)
        {
            var s = new DerivedStats();

            // ── HP e MP ────────────────────────────────────────────────────
            s.MaxHP = BASE_HP + (VIT * HP_PER_VIT) + (STR * HP_PER_STR)
                    + (level * HP_PER_LEVEL) + equip.HPBonus;
            s.MaxMP = BASE_MP + (INT * MP_PER_INT) + (DEX * MP_PER_DEX)
                    + (level * MP_PER_LEVEL) + equip.MPBonus;

            s.MaxHP = Math.Max(1f, s.MaxHP);
            s.MaxMP = Math.Max(1f, s.MaxMP);

            // ── Ataque e Defesa ────────────────────────────────────────────
            s.ATK  = ((STR * ATK_PER_STR)  + (DEX * ATK_PER_DEX)  + level + equip.ATK)  * buff.ATKMultiplier;
            s.MATK = ((INT * MATK_PER_INT) + (DEX * MATK_PER_DEX) + level + equip.MATK) * buff.ATKMultiplier;

            s.DEF  = ((VIT * DEF_PER_VIT)   + (STR * DEF_PER_STR)  + equip.DEF)  * buff.DEFMultiplier;
            s.MDEF = ((INT * MDEF_PER_INT)  + (VIT * MDEF_PER_VIT) + equip.MDEF) * buff.DEFMultiplier;

            // ── Velocidade ─────────────────────────────────────────────────
            s.ASPD      = Mathf.Clamp(BASE_ASPD + (AGI * ASPD_PER_AGI) + (DEX * ASPD_PER_DEX),
                                      MIN_ASPD, MAX_ASPD);
            s.MoveSpeed = Mathf.Clamp(BASE_MOVESPEED + (AGI * MOVE_PER_AGI),
                                      MIN_MOVESPEED, MAX_MOVESPEED);

            // ── Precisão ───────────────────────────────────────────────────
            s.HIT  = (DEX * HIT_PER_DEX)  + (LUK * HIT_PER_LUK);
            s.FLEE = (AGI * FLEE_PER_AGI) + (LUK * FLEE_PER_LUK);

            // ── Crítico ────────────────────────────────────────────────────
            s.CRIT = LUK * CRIT_PER_LUK;

            // CritDMG: 1.5x base + escalamento por LUK acima de 50
            float lukAbove50 = Math.Max(0f, LUK - 50f);
            s.CritDMG = Mathf.Clamp(MIN_CRIT_DMG + (lukAbove50 * CRITDMG_PER_LUK),
                                    MIN_CRIT_DMG, MAX_CRIT_DMG);

            // ── Regen (por tick de 5s) ─────────────────────────────────────
            s.HPRegen = (VIT * HPREGEN_PER_VIT) + (level * HPREGEN_PER_LVL);
            s.MPRegen = (INT * MPREGEN_PER_INT) + (level * MPREGEN_PER_LVL);

            // ── CastSpeed ─────────────────────────────────────────────────
            // effectiveCastTime = skill.CastTime / (1 + CastSpeed/100)
            // Lv1 DEX=12,INT=10: CastSpeed = 12*0.4+10*0.3 = 7.8 → -7.2% do CastTime
            s.CastSpeed = (DEX * CAST_PER_DEX) + (INT * CAST_PER_INT);

            // ── Penetração (AGORA USADA no cálculo de dano) ───────────────
            s.Penetration      = Mathf.Min(STR * PEN_PER_STR,  MAX_PENETRATION);
            s.MagicPenetration = Mathf.Min(INT * MPEN_PER_INT, MAX_PENETRATION);

            // ── Redução de dano (AGORA USADA no cálculo de dano recebido) ─
            s.DamageReduction = VIT * DMGRED_PER_VIT;

            // ── Resistências elementais ────────────────────────────────────
            s.ResistFire      = Mathf.Clamp(equip.ResistFire,      0f, MAX_RESIST);
            s.ResistIce       = Mathf.Clamp(equip.ResistIce,       0f, MAX_RESIST);
            s.ResistPoison    = Mathf.Clamp(equip.ResistPoison,    0f, MAX_RESIST);
            s.ResistLightning = Mathf.Clamp(equip.ResistLightning, 0f, MAX_RESIST);

            return s;
        }

        // ══════════════════════════════════════════════════════════════════
        // FÓRMULAS DE DANO — Penetração e DamageReduction integrados
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Calcula dano físico final.
        ///
        /// Pipeline:
        ///   1. Aplica DamageReduction do defensor (flat, antes da redução %)
        ///   2. Subtrai Penetração do atacante da DEF efetiva do defensor
        ///   3. Calcula redução percentual: DEF_efetiva / (DEF_efetiva + 100)
        ///   4. Aplica crítico se houver
        ///   5. Garante dano mínimo de 1
        ///
        /// atk: ATK do atacante (já inclui buffs e multiplicadores de skill)
        /// def: DEF do defensor
        /// penetration: Penetração física do atacante (de DerivedStats.Penetration)
        /// dmgReduction: Redução flat do defensor (de DerivedStats.DamageReduction)
        /// isCrit: se o ataque é crítico
        /// critDmgMult: multiplicador de crítico (de DerivedStats.CritDMG)
        /// </summary>
        public static float CalculatePhysicalDamage(
            float atk,
            float def,
            bool  isCrit,
            float critDmgMult   = 1.5f,
            float penetration   = 0f,
            float dmgReduction  = 0f)
        {
            // 1. DamageReduction flat do defensor
            float rawDmg = Math.Max(0f, atk - dmgReduction);

            // 2. DEF efetiva após penetração do atacante
            float effectiveDef = Math.Max(0f, def - penetration);

            // 3. Redução percentual por DEF
            float reduction = effectiveDef / (effectiveDef + 100f);
            float finalDmg  = rawDmg * (1f - reduction);

            // 4. Garantia de dano mínimo de 1 antes do crítico
            finalDmg = Math.Max(1f, finalDmg);

            // 5. Crítico
            if (isCrit) finalDmg *= critDmgMult;

            return Mathf.Floor(finalDmg);
        }

        /// <summary>
        /// Calcula dano mágico final.
        ///
        /// Pipeline idêntico ao físico, usando MDEF e MagicPenetration.
        /// </summary>
        public static float CalculateMagicDamage(
            float matk,
            float mdef,
            bool  isCrit,
            float critDmgMult      = 1.5f,
            float magicPenetration = 0f,
            float dmgReduction     = 0f)
        {
            float rawDmg       = Math.Max(0f, matk - dmgReduction * 0.5f); // redução física é menos eficaz vs magia
            float effectiveMdef = Math.Max(0f, mdef - magicPenetration);
            float reduction     = effectiveMdef / (effectiveMdef + 100f);
            float finalDmg      = rawDmg * (1f - reduction);

            finalDmg = Math.Max(1f, finalDmg);
            if (isCrit) finalDmg *= critDmgMult;

            return Mathf.Floor(finalDmg);
        }

        /// <summary>
        /// Calcula o CastTime efetivo de uma skill, levando em conta o CastSpeed do caster.
        ///
        /// effectiveCastTime = baseCastTime / (1 + CastSpeed / 100)
        ///
        /// Exemplo: baseCastTime=2.0s, CastSpeed=50 → effectiveCastTime = 2.0/1.5 = 1.33s
        /// </summary>
        public static float CalculateEffectiveCastTime(float baseCastTime, float castSpeed)
        {
            if (baseCastTime <= 0f) return 0f;
            float divisor = 1f + Mathf.Max(0f, castSpeed) / 100f;
            return baseCastTime / divisor;
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

        /// <summary>
        /// THREAD-SAFE. CORRIGIDO: proteção contra NaN quando hit e flee são 0.
        ///
        /// Se hit=0 e flee=0, a divisão 0/0 produzia NaN em .NET.
        /// Mathf.Clamp(NaN, 5, 95) retorna NaN (não 5), causando todos os ataques
        /// a errar (NaN < qualquer número = false).
        ///
        /// SOLUÇÃO: guard explícito antes da divisão.
        /// </summary>
        public static bool RollHit(float hit, float flee, System.Random rng = null)
        {
            float hitChance;

            // Guard: evita divisão por zero e NaN
            if (hit <= 0f && flee <= 0f)
            {
                hitChance = 50f; // hit base de 50% se ambos são 0
            }
            else
            {
                float total = hit + flee;
                hitChance = Mathf.Clamp((hit / total) * 100f, 5f, 95f);
            }

            float roll = rng != null
                ? (float)(rng.NextDouble() * 100.0)
                : UnityEngine.Random.Range(0f, 100f);
            return roll < hitChance;
        }

        // ══════════════════════════════════════════════════════════════════
        // HELPERS DE DEBUG (somente Editor)
        // ══════════════════════════════════════════════════════════════════

#if UNITY_EDITOR
        /// <summary>
        /// Retorna um resumo legível dos stats derivados para debug no Inspector.
        /// </summary>
        public static string DebugSummary(DerivedStats s, int level)
        {
            return $"[Lv{level}] HP:{s.MaxHP:0} MP:{s.MaxMP:0} | " +
                   $"ATK:{s.ATK:0} MATK:{s.MATK:0} DEF:{s.DEF:0} MDEF:{s.MDEF:0} | " +
                   $"ASPD:{s.ASPD:0.00}/s ({1f/s.ASPD:0.00}s) SPD:{s.MoveSpeed:0.0} | " +
                   $"HIT:{s.HIT:0} FLEE:{s.FLEE:0} CRIT:{s.CRIT:0.0}% CritDMG:{s.CritDMG:0.00}x | " +
                   $"Pen:{s.Penetration:0} MagPen:{s.MagicPenetration:0} DmgRed:{s.DamageReduction:0} | " +
                   $"HPRegen:{s.HPRegen:0.0}/5s MPRegen:{s.MPRegen:0.0}/5s | " +
                   $"CastSpd:{s.CastSpeed:0.0}";
        }
#endif
    }
}
