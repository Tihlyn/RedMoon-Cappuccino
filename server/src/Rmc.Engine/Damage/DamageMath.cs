namespace Rmc.Engine.Damage;

public enum AttackType
{
    Weaponskill,
    Spell,
    Ability,
    AutoAttack,
}

/// <summary>
/// Per-hit damage before crit/direct hit (Allagan Studies; xivgear's baseDamageFull), in exact
/// integer arithmetic on the engine's scaled multipliers. Two orders exist: casters and healers
/// multiply potency into weapon damage first; everyone else applies main stat first.
/// </summary>
public static class DamageMath
{
    /// <param name="potency">Action potency (per tick for damage over time).</param>
    /// <param name="autoDirectHit">The action always direct hits: determination gains the DH-scaled bonus.</param>
    /// <param name="damageOverTime">A tick of damage over time: scaled by skill or spell speed.</param>
    public static long BaseDamage(GearsetStats s, int potency, AttackType type, bool autoDirectHit, bool damageOverTime, int traitPercent)
    {
        var isAuto = type == AttackType.AutoAttack;
        long mainPercent = isAuto ? s.AutoAttackStatPercent : s.MainStatPercent;
        long wdPercent = isAuto ? s.AutoAttackPercent : s.WeaponDamagePercent;
        long detPerMille = s.DeterminationPerMille + (autoDirectHit ? s.AutoDirectHitBonusPerMille : 0);
        long tncPerMille = s.TenacityPerMille;
        long speedPerMille = isAuto
            ? s.SkillSpeedDotPerMille
            : damageOverTime
                ? type == AttackType.Weaponskill ? s.SkillSpeedDotPerMille : s.SpellSpeedDotPerMille
                : 1000;
        long trait = isAuto ? 100 : traitPercent;

        long afterSpeed;
        if (s.Role is "Caster" or "Healer" && !isAuto)
        {
            var apDetHundredths = mainPercent * detPerMille / 1000;          // flp(2, ms × det)
            var wdPotency = wdPercent * potency / 100;                       // fl(wd × potency)
            var basePotency = apDetHundredths * wdPotency / 100;
            var afterTnc = basePotency * tncPerMille / 1000;
            afterSpeed = afterTnc * speedPerMille / 1000;
        }
        else
        {
            var basePotency = potency * mainPercent / 100;
            var afterDet = basePotency * detPerMille / 1000;
            var afterTnc = afterDet * tncPerMille / 1000;
            var afterWd = afterTnc * wdPercent / 100;
            afterSpeed = afterWd * speedPerMille / 1000;
        }

        var final = afterSpeed * trait / 100 + (potency < 100 ? 1 : 0);
        return Math.Max(1, final);
    }

    /// <summary>
    /// Expected multiplier from crit and direct hit. Forced crits/direct hits still gain from
    /// crit/DH rate buffs (each point of buffed rate converts to damage, xivgear's autoCritBuffDmg).
    /// </summary>
    public static double CritDirectHitFactor(GearsetStats s, double critRateBonus, double dhRateBonus, bool autoCrit, bool autoDirectHit)
    {
        var critMulti = s.CritDamagePerMille / 1000.0;
        double crit;
        if (autoCrit)
            crit = critMulti * Floor3(1 + (critMulti - 1) * critRateBonus);
        else
            crit = 1 + Math.Min(1, s.CritRatePerMille / 1000.0 + critRateBonus) * (critMulti - 1);

        double dh;
        if (autoDirectHit)
            dh = 1.25 * Floor3(1 + 0.25 * dhRateBonus);
        else
            dh = 1 + Math.Min(1, s.DirectHitRatePerMille / 1000.0 + dhRateBonus) * 0.25;

        return crit * dh;
    }

    private static double Floor3(double x) => Math.Floor(x * 1000 + 1e-7) / 1000;
}
