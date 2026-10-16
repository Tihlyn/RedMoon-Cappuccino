using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Dancer, level 100 (Dawntrail). Standard Step on cooldown (Finishing Move in place of it while
/// Flourish's proc is up), Technical Step every two minutes with Devilment, the potion and Flourish
/// behind it; Esprit spenders and procs by priority, Cascade → Fountain otherwise.
/// Random procs (Silken Symmetry/Flow, feathers, Threefold Fan Dance: 50% each) count as their
/// expected value: half a proc per chance, spent once a whole one has built up.
/// </summary>
public sealed class DncRotation : Rotation
{
    private const int StepMs = 1000;
    private const int DanceGcdMs = 1500;
    /// <summary>Dance partner's Esprit: 20% chance of 10 per weaponskill or spell, about one every 2.5s.</summary>
    private const double PartnerEspritPerGcd = 2;

    public override string Job => "DNC";
    public override int TraitPercent => 120;
    public override int AutoAttackPotency => 80;

    // GCDs
    private readonly SimAbility cascade = Gcd("Cascade", 15989, 220);
    private readonly SimAbility fountain = Gcd("Fountain", 15990, 280, "Combo Potency");
    private readonly SimAbility reverseCascade = Gcd("Reverse Cascade", 15991, 280);
    private readonly SimAbility fountainfall = Gcd("Fountainfall", 15992, 340);
    private readonly SimAbility saberDance = Gcd("Saber Dance", 16005, 540);
    private readonly SimAbility danceOfTheDawn = Gcd("Dance of the Dawn", 36985, 1000);
    private readonly SimAbility starfallDance = Gcd("Starfall Dance", 25792, 600, autoCrit: true, autoDh: true);
    private readonly SimAbility lastDance = Gcd("Last Dance", 36983, 540);
    private readonly SimAbility tillana = Gcd("Tillana", 25790, 600);
    private readonly SimAbility standardStep = Gcd("Standard Step", 15997, 0, recastMs: DanceGcdMs, fixedRecast: true, cooldownMs: 30_000, group: "Standard Step");
    private readonly SimAbility technicalStep = Gcd("Technical Step", 15998, 0, recastMs: DanceGcdMs, fixedRecast: true, cooldownMs: 120_000);
    private readonly SimAbility step = Gcd("Step", 15999, 0, recastMs: StepMs, fixedRecast: true);
    private readonly SimAbility standardFinish = Gcd("Standard Finish", 16192, 850, "2 Steps", recastMs: DanceGcdMs, fixedRecast: true,
        grants: ["Standard Finish", "Last Dance Ready"]);
    private readonly SimAbility technicalFinish = Gcd("Technical Finish", 16196, 1300, "4 Steps", recastMs: DanceGcdMs, fixedRecast: true,
        grants: ["Technical Finish", "Flourishing Finish", "Dance of the Dawn Ready"]);
    private readonly SimAbility finishingMove = Gcd("Finishing Move", 36984, 850, recastMs: DanceGcdMs, fixedRecast: true,
        cooldownMs: 30_000, group: "Standard Step", grants: ["Standard Finish", "Last Dance Ready"]);

    // oGCDs
    private readonly SimAbility devilment = Ogcd("Devilment", 16011, 0, 120_000, grants: ["Devilment", "Flourishing Starfall"]);
    private readonly SimAbility flourish = Ogcd("Flourish", 16013, 0, 60_000, grants: ["Fourfold Fan Dance", "Finishing Move Ready"]);
    private readonly SimAbility fanDance = Ogcd("Fan Dance", 16007, 180, 0);
    private readonly SimAbility fanDance3 = Ogcd("Fan Dance III", 16009, 220, 0);
    private readonly SimAbility fanDance4 = Ogcd("Fan Dance IV", 25791, 460, 0);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Standard Finish", DurationMs = 60_000, DamagePercent = 5 },
        new() { Name = "Technical Finish", DurationMs = 20_000, DamagePercent = 5 },
        new() { Name = "Devilment", DurationMs = 20_000, CritRate = 20, DirectHitRate = 20 },
        new() { Name = "Last Dance Ready", DurationMs = 30_000 },
        new() { Name = "Flourishing Finish", DurationMs = 30_000 },
        new() { Name = "Dance of the Dawn Ready", DurationMs = 30_000 },
        new() { Name = "Flourishing Starfall", DurationMs = 20_000 },
        new() { Name = "Fourfold Fan Dance", DurationMs = 30_000 },
        new() { Name = "Finishing Move Ready", DurationMs = 30_000 },
    ];

    private bool comboReady;
    private double symmetry, flow, feathers, threefold;
    private int esprit;
    private double partnerEsprit;
    private int stepsLeft;
    private SimAbility? dancing;

    public DncRotation()
    {
        abilities =
        [
            cascade, fountain, reverseCascade, fountainfall, saberDance, danceOfTheDawn, starfallDance, lastDance, tillana,
            standardStep, technicalStep, step, standardFinish, technicalFinish, finishingMove,
            devilment, flourish, fanDance, fanDance3, fanDance4,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboReady = false;
        symmetry = flow = feathers = threefold = 0;
        esprit = 0;
        partnerEsprit = 0;
        stepsLeft = 0;
        dancing = null;
    }

    public override SimAbility? NextGcd(SimState s)
    {
        if (dancing != null)
            return stepsLeft > 0 ? step : dancing == technicalStep ? technicalFinish : standardFinish;

        var inTechnical = s.HasBuff("Technical Finish");
        if (s.IsReady(technicalStep) && s.HasBuff("Standard Finish"))
            return technicalStep;
        if (s.IsReady(standardStep))
        {
            if (s.HasBuff("Finishing Move Ready"))
                return finishingMove;
            // Hold Standard Step for the burst when Technical Step is about to come back.
            if (!inTechnical && (s.CooldownRemaining(technicalStep) > 5000 || !s.HasBuff("Standard Finish")))
                return standardStep;
        }
        if (s.HasBuff("Dance of the Dawn Ready") && esprit >= 50)
            return danceOfTheDawn;
        if (s.HasBuff("Flourishing Starfall"))
            return starfallDance;
        if (s.HasBuff("Flourishing Finish") && esprit <= 50)
            return tillana;
        if (esprit >= 80 || (inTechnical && esprit >= 50))
            return saberDance;
        if (s.HasBuff("Last Dance Ready"))
            return lastDance;
        if (s.HasBuff("Flourishing Finish"))
            return tillana;
        if (flow >= 1)
            return fountainfall;
        if (symmetry >= 1)
            return reverseCascade;
        return comboReady ? fountain : cascade;
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (dancing != null)
            return null;
        if (s.IsReady(potion) && s.CooldownRemaining(technicalStep) < 3000 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        var inTechnical = s.HasBuff("Technical Finish");
        if (inTechnical && s.IsReady(devilment))
            return devilment;
        if (s.IsReady(flourish) && (inTechnical || s.CooldownRemaining(technicalStep) > 20_000)
            && !s.HasBuff("Finishing Move Ready") && !s.HasBuff("Fourfold Fan Dance") && threefold < 1)
            return flourish;
        if (s.HasBuff("Fourfold Fan Dance"))
            return fanDance4;
        if (threefold >= 1)
            return fanDance3;
        if (feathers >= 4 || (feathers >= 1 && inTechnical))
            return fanDance;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a.Gcd && a != step && a != standardStep && a != technicalStep)
        {
            // Esprit (under Standard/Technical Finish's Esprit): 5 for combo actions, 10 for procs.
            var gain = a == cascade || a == fountain ? 5 : a == reverseCascade || a == fountainfall ? 10 : 0;
            if (s.HasBuff("Standard Finish") || s.HasBuff("Technical Finish"))
            {
                partnerEsprit += PartnerEspritPerGcd;
                var whole = (int)partnerEsprit;
                partnerEsprit -= whole;
                gain += whole;
            }
            esprit = Math.Min(100, esprit + gain);
        }

        if (a == cascade)
        {
            comboReady = true;
            symmetry = Math.Min(1.5, symmetry + 0.5);
        }
        else if (a == fountain)
        {
            comboReady = false;
            flow = Math.Min(1.5, flow + 0.5);
        }
        else if (a == reverseCascade)
        {
            symmetry -= 1;
            feathers = Math.Min(4, feathers + 0.5);
        }
        else if (a == fountainfall)
        {
            flow -= 1;
            feathers = Math.Min(4, feathers + 0.5);
        }
        else if (a == saberDance) esprit -= 50;
        else if (a == danceOfTheDawn)
        {
            esprit -= 50;
            s.Remove("Dance of the Dawn Ready");
        }
        else if (a == tillana)
        {
            esprit = Math.Min(100, esprit + 50);
            s.Remove("Flourishing Finish");
        }
        else if (a == starfallDance) s.Remove("Flourishing Starfall");
        else if (a == lastDance) s.Remove("Last Dance Ready");
        else if (a == finishingMove) s.Remove("Finishing Move Ready");
        else if (a == standardStep)
        {
            dancing = standardStep;
            stepsLeft = 2;
        }
        else if (a == technicalStep)
        {
            dancing = technicalStep;
            stepsLeft = 4;
        }
        else if (a == step) stepsLeft--;
        else if (a == standardFinish || a == technicalFinish) dancing = null;
        else if (a == flourish)
        {
            // Flourishing Symmetry, Flow and Threefold Fan Dance are guaranteed.
            symmetry = Math.Max(symmetry, 1);
            flow = Math.Max(flow, 1);
            threefold = Math.Max(threefold, 1);
        }
        else if (a == fanDance)
        {
            feathers -= 1;
            threefold = Math.Min(1.5, threefold + 0.5);
        }
        else if (a == fanDance3) threefold -= 1;
        else if (a == fanDance4) s.Remove("Fourfold Fan Dance");
    }
}
