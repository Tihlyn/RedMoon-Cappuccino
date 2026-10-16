using Rmc.Engine.Damage;
using Rmc.GameData;

namespace Rmc.Engine.Sim;

/// <summary>The state a rotation reads when choosing its next action.</summary>
public sealed class SimState
{
    private readonly Simulation sim;
    internal SimState(Simulation sim) => this.sim = sim;

    public int Now => sim.Now;
    public int FightEndMs => sim.EndMs;
    public int TimeUntilGcd => Math.Max(0, sim.NextGcdAt - sim.Now);
    /// <summary>Current GCD recast for a 2.5s base, with active haste.</summary>
    public int GcdMs => sim.ScaledMs(2500);
    /// <summary>The set's critical hit rate (0..1), for rotations whose gauges fill on crits.</summary>
    public double CritRate => sim.CritRate;

    public bool IsReady(SimAbility a) => sim.ChargesOf(a) > 0;
    public int Charges(SimAbility a) => sim.ChargesOf(a);
    /// <summary>Time until the next charge of <paramref name="a"/> is back (0 when one is ready).</summary>
    public int CooldownRemaining(SimAbility a) => sim.CooldownRemaining(a);
    /// <summary>Brings the next charge of <paramref name="a"/> forward (Blazing Shot on Double Check, ...).</summary>
    public void ReduceCooldown(SimAbility a, int ms) => sim.ReduceCooldown(a, ms);

    public bool HasBuff(string name) => sim.BuffExpiry(name) > sim.Now;
    public int BuffRemaining(string name) => Math.Max(0, sim.BuffExpiry(name) - sim.Now);
    public int BuffStacks(string name) => HasBuff(name) ? sim.BuffStacks(name) : 0;
    public void Grant(string name) => sim.GrantBuff(name);
    /// <summary>Grants (or resets) a buff with a duration other than its default.</summary>
    public void Grant(string name, int durationMs) => sim.GrantBuff(name, durationMs);
    public void Remove(string name) => sim.RemoveBuff(name);

    public bool DotActive(string name) => DotRemaining(name) > 0;
    public int DotRemaining(string name) => sim.DotRemaining(name);
    /// <summary>Re-applies a running damage over time from <paramref name="source"/> with today's
    /// buffs and a full duration (Iron Jaws); does nothing if it isn't running.</summary>
    public void RefreshDot(string name, SimAbility source) => sim.RefreshDot(name, source);
}

/// <summary>
/// Runs a rotation over a fight and adds up expected damage. Deterministic: every hit counts its
/// expected crit/direct-hit value rather than rolling, so results are stable and comparable.
/// </summary>
public sealed class Simulation
{
    private const int TickMs = 3000;
    private const int CasterTaxMs = 100;
    private const int BurstWindowMs = 20_000;
    private const int BurstPeriodMs = 120_000;

    private readonly GearsetStats stats;
    private readonly GearsetStats? potted;
    private readonly int potionDurationMs;
    private readonly Rotation rotation;
    private readonly SimSettings settings;
    private readonly Dictionary<string, SimBuff> buffDefs;
    private readonly SimAbility autoAttack = new() { Name = "Auto-attack", Type = AttackType.AutoAttack };

    private readonly Dictionary<string, (int Charges, int NextChargeAt)> cooldowns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int ExpiresAt, int Stacks)> buffs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveDot> dots = new(StringComparer.Ordinal);
    private readonly SimResult result = new();

    internal int Now;
    internal int EndMs;
    internal int NextGcdAt;
    private int lockUntil;
    private int nextAutoAt;
    private int nextTickAt = TickMs;

    private sealed record ActiveDot(SimAbility Source, SimDot Dot, int ExpiresAt, HitResult Tick);

    public Simulation(GearsetStats stats, GearsetStats? potted, int potionDurationMs, Rotation rotation, SimSettings settings)
    {
        this.stats = stats;
        this.potted = potted;
        this.potionDurationMs = potionDurationMs;
        this.rotation = rotation;
        this.settings = settings;
        buffDefs = rotation.Buffs.ToDictionary(b => b.Name, StringComparer.Ordinal);
        buffDefs["Medicated"] = new SimBuff { Name = "Medicated", DurationMs = potionDurationMs };
    }

    /// <summary>Simulates a gear set: stats with and without the job's best potion.</summary>
    public static SimResult Run(StatEngine engine, GearsetInput input, Rotation rotation, SimSettings? settings = null)
    {
        settings ??= new SimSettings();
        var stats = engine.Compute(input);
        var medicine = settings.UsePotion && rotation.UsesPotion ? engine.BestMedicine(input.Job) : null;
        var potted = medicine != null ? engine.WithMedicine(stats, medicine) : null;
        return new Simulation(stats, potted, (medicine?.DurationSeconds ?? 30) * 1000, rotation, settings).Run();
    }

    public SimResult Run()
    {
        rotation.Reset();
        EndMs = (int)Math.Round(settings.FightSeconds * 1000);
        var state = new SimState(this);
        var idleGuard = 0;

        while (Now < EndMs)
        {
            ProcessTimedEvents();

            var acted = false;
            if (Now >= lockUntil)
            {
                if (Now >= NextGcdAt)
                {
                    var gcd = rotation.NextGcd(state);
                    if (gcd != null)
                    {
                        Use(gcd, state);
                        acted = true;
                    }
                }
                else
                {
                    var ogcd = rotation.NextOgcd(state);
                    if (ogcd != null && ChargesOf(ogcd) > 0 && Now + ogcd.AnimationLockMs <= NextGcdAt + settings.AcceptableClipMs)
                    {
                        Use(ogcd, state);
                        acted = true;
                    }
                }
            }

            if (acted)
            {
                idleGuard = 0;
                continue;
            }

            // Jump to the next moment something can change.
            var next = new[] { NextGcdAt, lockUntil, nextTickAt, nextAutoAt, EndMs }.Where(t => t > Now).DefaultIfEmpty(Now + 100).Min();
            if (Now >= NextGcdAt && Now >= lockUntil)
                next = Math.Min(next, Now + 100); // rotation is waiting on a cooldown or buff
            if (++idleGuard > 1_000_000)
                throw new InvalidOperationException($"{rotation.Job} simulation made no progress at {Now}ms.");
            Now = next;
        }

        ProcessTimedEvents();
        result.Dps = result.TotalDamage / settings.FightSeconds;
        return result;
    }

    private void ProcessTimedEvents()
    {
        while (nextTickAt <= Now && nextTickAt < EndMs)
        {
            foreach (var (name, dot) in dots.ToList())
            {
                if (dot.ExpiresAt < nextTickAt)
                {
                    dots.Remove(name);
                    continue;
                }
                Record(dot.Dot.Name, dot.Tick, nextTickAt, count: false);
            }
            nextTickAt += TickMs;
        }

        if (rotation.AutoAttackPotency > 0 && stats.WeaponDelayMs > 0)
        {
            while (nextAutoAt <= Now && nextAutoAt < EndMs)
            {
                Record(autoAttack.Name, Hit(autoAttack, rotation.AutoAttackPotency, isDot: false, consume: false), nextAutoAt, count: true);
                // Auto-attack-only haste (Riddle of Wind) multiplies with the rest.
                nextAutoAt += Math.Max(1, stats.WeaponDelayMs * (100 - rotation.TraitHastePercent - BuffHaste()) / 100 * (100 - AutoBuffHaste()) / 100);
            }
        }
    }

    private void Use(SimAbility a, SimState state)
    {
        var at = Now;
        if (a.Gcd)
        {
            var cast = a.CastMs > 0 ? ScaledMs(a.CastMs) : 0;
            NextGcdAt = Now + (a.FixedRecast ? a.RecastMs : ScaledMs(a.RecastMs));
            lockUntil = Now + Math.Max(cast > 0 ? cast + CasterTaxMs : 0, a.AnimationLockMs);
            at = Now + cast;
            result.GcdCount++;
            // An auto-attack can't land mid-cast; it waits for the cast to finish.
            if (cast > 0 && nextAutoAt < Now + cast)
                nextAutoAt = Now + cast;
        }
        else
        {
            lockUntil = Now + a.AnimationLockMs;
        }

        if (a.CooldownMs > 0)
            ConsumeCharge(a);

        if (a.IsPotion)
            GrantBuff("Medicated");

        var potency = a.Potency + rotation.PotencyBonus(a, state);
        if (potency > 0)
            Record(a.Name, Hit(a, potency, isDot: false, consume: true), at, count: true);
        else
            Count(a.Name);

        if (a.Dot is { } dot)
            dots[dot.Name] = new ActiveDot(a, dot, at + dot.DurationMs, Hit(a, dot.Potency, isDot: true, consume: false));

        foreach (var grant in a.Grants)
            GrantBuff(grant);

        rotation.OnUse(a, state);
    }

    /// <summary>Expected damage of one hit with the buffs active right now, and its stat-independent context.</summary>
    private HitResult Hit(SimAbility a, int potency, bool isDot, bool consume)
    {
        var s = potted != null && BuffExpiry("Medicated") > Now ? potted : stats;
        var multiplier = 1.0;
        double crit = 0, dh = 0;
        var forceCrit = false;
        var forceDh = false;

        foreach (var (name, (expires, stacks)) in buffs.ToList())
        {
            if (expires <= Now || !buffDefs.TryGetValue(name, out var def) || !def.Affects(a))
                continue;
            multiplier *= 1 + def.DamagePercent / 100.0;
            crit += def.CritRate / 100.0;
            dh += def.DirectHitRate / 100.0;
            if (!isDot && consume && (def.ForceCrit || def.ForceDirectHit))
            {
                forceCrit |= def.ForceCrit;
                forceDh |= def.ForceDirectHit;
                buffs.Remove(name);
            }
            else if (!isDot && consume && def.Stacks > 0)
            {
                if (stacks <= 1) buffs.Remove(name);
                else buffs[name] = (expires, stacks - 1);
            }
        }

        if (InPartyBurst())
        {
            foreach (var b in settings.PartyBuffs)
            {
                multiplier *= 1 + b.DamagePercent / 100.0;
                crit += b.CritRate / 100.0;
                dh += b.DirectHitRate / 100.0;
            }
        }

        var autoCrit = a.AutoCrit || forceCrit;
        var autoDh = a.AutoDirectHit || forceDh;
        var baseDamage = DamageMath.BaseDamage(s, potency, a.Type, autoDh, isDot, rotation.TraitPercent);
        var factor = a.CannotCritOrDh ? 1 : DamageMath.CritDirectHitFactor(s, crit, dh, autoCrit, autoDh);
        var weight = multiplier * a.DamageMultiplier;
        var key = new HitKey(potency, a.Type, autoCrit, autoDh, isDot, a.CannotCritOrDh, ReferenceEquals(s, potted),
            (int)Math.Round(crit * 10_000), (int)Math.Round(dh * 10_000));
        return new HitResult(baseDamage * factor * weight, key, weight);
    }

    private readonly record struct HitResult(double Damage, HitKey Key, double Weight);

    private bool InPartyBurst()
        => settings.PartyBuffs.Count > 0 && Now >= settings.PartyBurstStartMs
           && (Now - settings.PartyBurstStartMs) % BurstPeriodMs < BurstWindowMs;

    private void Record(string name, HitResult hit, int at, bool count)
    {
        if (at >= EndMs)
            return;
        var damage = hit.Damage;
        if (settings.RecordHits)
            result.Hits[hit.Key] = result.Hits.TryGetValue(hit.Key, out var w) ? w + hit.Weight : hit.Weight;
        result.TotalDamage += damage;
        var total = result.ByAbility.TryGetValue(name, out var t) ? t : result.ByAbility[name] = new AbilityTotal();
        total.Damage += damage;
        if (count)
            total.Uses++;
        if (settings.RecordTimeline)
            result.Timeline.Add(new SimEvent(at, name, damage));
    }

    private void Count(string name)
    {
        var total = result.ByAbility.TryGetValue(name, out var t) ? t : result.ByAbility[name] = new AbilityTotal();
        total.Uses++;
        if (settings.RecordTimeline)
            result.Timeline.Add(new SimEvent(Now, name, 0));
    }

    // --- speed and haste ---------------------------------------------------------------------

    internal int ScaledMs(int baseMs)
    {
        var speed = stats.Final.GetValueOrDefault(rotation.SpeedStat);
        return stats.GcdFor(speed, baseMs, rotation.TraitHastePercent + BuffHaste()) * 10;
    }

    internal double CritRate => stats.CritRatePerMille / 1000.0;

    private int AutoBuffHaste() => buffs.Where(kv => kv.Value.ExpiresAt > Now && buffDefs.TryGetValue(kv.Key, out var d) && d.AutoAttackHastePercent > 0)
        .Sum(kv => buffDefs[kv.Key].AutoAttackHastePercent);

    private int BuffHaste() => buffs.Where(kv => kv.Value.ExpiresAt > Now && buffDefs.TryGetValue(kv.Key, out var d) && d.HastePercent > 0)
        .Sum(kv => buffDefs[kv.Key].HastePercent);

    // --- cooldowns ---------------------------------------------------------------------------

    private string CooldownKey(SimAbility a) => a.CooldownGroup ?? a.Name;

    internal int ChargesOf(SimAbility a)
    {
        if (a.CooldownMs <= 0)
            return 1;
        Refill(a);
        return cooldowns.TryGetValue(CooldownKey(a), out var c) ? c.Charges : a.Charges;
    }

    internal int CooldownRemaining(SimAbility a)
    {
        if (ChargesOf(a) > 0)
            return 0;
        return Math.Max(0, cooldowns[CooldownKey(a)].NextChargeAt - Now);
    }

    private void Refill(SimAbility a)
    {
        var key = CooldownKey(a);
        if (!cooldowns.TryGetValue(key, out var c))
            return;
        while (c.Charges < a.Charges && c.NextChargeAt <= Now)
        {
            c.Charges++;
            c.NextChargeAt = c.Charges < a.Charges ? c.NextChargeAt + a.CooldownMs : int.MaxValue;
        }
        cooldowns[key] = c;
    }

    internal void ReduceCooldown(SimAbility a, int ms)
    {
        var key = CooldownKey(a);
        if (!cooldowns.TryGetValue(key, out var c) || c.NextChargeAt == int.MaxValue)
            return;
        c.NextChargeAt -= ms;
        cooldowns[key] = c;
        Refill(a);
    }

    private void ConsumeCharge(SimAbility a)
    {
        Refill(a);
        var key = CooldownKey(a);
        var c = cooldowns.TryGetValue(key, out var existing) ? existing : (Charges: a.Charges, NextChargeAt: int.MaxValue);
        // The recharge timer starts when the pool drops below full.
        if (c.Charges == a.Charges)
            c.NextChargeAt = Now + a.CooldownMs;
        c.Charges--;
        cooldowns[key] = c;
    }

    // --- buffs and damage over time ------------------------------------------------------------

    internal void GrantBuff(string name, int? durationMs = null)
    {
        if (!buffDefs.TryGetValue(name, out var def))
            throw new InvalidOperationException($"{rotation.Job}: unknown buff '{name}'.");
        buffs[name] = (Now + (durationMs ?? def.DurationMs), Math.Max(1, def.Stacks));
    }

    internal void RemoveBuff(string name) => buffs.Remove(name);
    internal int BuffExpiry(string name) => buffs.TryGetValue(name, out var b) ? b.ExpiresAt : 0;
    internal int BuffStacks(string name) => buffs.TryGetValue(name, out var b) ? b.Stacks : 0;
    internal void RefreshDot(string name, SimAbility source)
    {
        if (source.Dot is not { } dot || !dots.ContainsKey(name))
            return;
        dots[name] = new ActiveDot(source, dot, Now + dot.DurationMs, Hit(source, dot.Potency, isDot: true, consume: false));
    }

    internal int DotRemaining(string name) => dots.TryGetValue(name, out var d) ? Math.Max(0, d.ExpiresAt - Now) : 0;
}
