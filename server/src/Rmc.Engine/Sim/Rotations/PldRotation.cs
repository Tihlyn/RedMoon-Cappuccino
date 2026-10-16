using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Paladin, level 100 (Dawntrail). The seven-GCD loop: Fast Blade → Riot Blade → Royal Authority,
/// Atonement → Supplication → Sepulchre, and an instant Holy Spirit from Divine Might. Fight or
/// Flight every minute (the potion every six) with Goring Blade, Imperator into Confiteor → Blade of
/// Faith → Truth → Valor and Blade of Honor; Expiacion, Circle of Scorn and Intervene on cooldown.
/// The opener's Fight or Flight follows Royal Authority, as in xivgear's PLD sim.
/// </summary>
public sealed class PldRotation : Rotation
{
    public override string Job => "PLD";

    // GCDs
    private readonly SimAbility fastBlade = Gcd("Fast Blade", 9, 220);
    private readonly SimAbility riotBlade = Gcd("Riot Blade", 15, 330, "Combo Potency");
    private readonly SimAbility royalAuthority = Gcd("Royal Authority", 3539, 460, "Combo Potency");
    private readonly SimAbility atonement = Gcd("Atonement", 16460, 460);
    private readonly SimAbility supplication = Gcd("Supplication", 36918, 500);
    private readonly SimAbility sepulchre = Gcd("Sepulchre", 36919, 540);
    private readonly SimAbility holySpirit = Gcd("Holy Spirit", 7384, 500, "Divine Might Potency", AttackType.Spell);
    private readonly SimAbility goringBlade = Gcd("Goring Blade", 3538, 700);
    private readonly SimAbility confiteor = Gcd("Confiteor", 16459, 1000, "Requiescat Potency", AttackType.Spell);
    private readonly SimAbility bladeOfFaith = Gcd("Blade of Faith", 25748, 760, "Requiescat Potency", AttackType.Spell);
    private readonly SimAbility bladeOfTruth = Gcd("Blade of Truth", 25749, 880, "Requiescat Potency", AttackType.Spell);
    private readonly SimAbility bladeOfValor = Gcd("Blade of Valor", 25750, 1000, "Requiescat Potency", AttackType.Spell);

    // oGCDs
    private readonly SimAbility fightOrFlight = Ogcd("Fight or Flight", 20, 0, 60_000, grants: ["Fight or Flight", "Goring Blade Ready"]);
    private readonly SimAbility imperator = Ogcd("Imperator", 36921, 580, 60_000);
    private readonly SimAbility bladeOfHonor = Ogcd("Blade of Honor", 36922, 1000, 1000);
    private readonly SimAbility expiacion = Ogcd("Expiacion", 25747, 450, 30_000);
    private readonly SimAbility circleOfScorn = Ogcd("Circle of Scorn", 23, 140, 30_000,
        dot: new SimDot { Name = "Circle of Scorn (DoT)", ActionId = 23, Potency = 30, DurationMs = 15_000 });
    private readonly SimAbility intervene = Ogcd("Intervene", 16461, 150, 30_000, charges: 2);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Fight or Flight", DurationMs = 20_000, DamagePercent = 25 },
        new() { Name = "Goring Blade Ready", DurationMs = 30_000 },
    ];

    private int comboStep;      // Fast Blade combo
    private int atonementStep;  // 1 Atonement, 2 Supplication, 3 Sepulchre ready
    private bool divineMight;
    private int requiescatStep; // 1 Confiteor ... 4 Blade of Valor
    private bool honorReady;

    public PldRotation()
    {
        abilities =
        [
            fastBlade, riotBlade, royalAuthority, atonement, supplication, sepulchre, holySpirit, goringBlade,
            confiteor, bladeOfFaith, bladeOfTruth, bladeOfValor,
            fightOrFlight, imperator, bladeOfHonor, expiacion, circleOfScorn, intervene,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        comboStep = atonementStep = requiescatStep = 0;
        divineMight = honorReady = false;
    }

    public override SimAbility? NextGcd(SimState s)
    {
        if (requiescatStep > 0)
            return requiescatStep switch { 1 => confiteor, 2 => bladeOfFaith, 3 => bladeOfTruth, _ => bladeOfValor };
        if (s.HasBuff("Goring Blade Ready") && s.HasBuff("Fight or Flight"))
            return goringBlade;
        if (atonementStep > 0)
            return atonementStep switch { 1 => atonement, 2 => supplication, _ => sepulchre };
        if (divineMight)
            return holySpirit;
        return comboStep switch { 1 => riotBlade, 2 => royalAuthority, _ => fastBlade };
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (honorReady)
            return bladeOfHonor;
        // The first Fight or Flight follows the opener's Royal Authority; then on cooldown.
        var ready = s.IsReady(fightOrFlight) && (s.Now > 20_000 || comboStep == 0 && atonementStep == 1);
        if (s.IsReady(potion) && ready && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (ready)
            return fightOrFlight;
        if (s.IsReady(imperator) && s.HasBuff("Fight or Flight"))
            return imperator;
        var burst = s.HasBuff("Fight or Flight") || s.CooldownRemaining(fightOrFlight) > 25_000;
        if (s.IsReady(circleOfScorn) && burst)
            return circleOfScorn;
        if (s.IsReady(expiacion) && burst)
            return expiacion;
        if (s.IsReady(intervene) && (s.HasBuff("Fight or Flight") || s.Charges(intervene) == 2 && s.CooldownRemaining(fightOrFlight) > 25_000))
            return intervene;
        return null;
    }

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == fastBlade) comboStep = 1;
        else if (a == riotBlade) comboStep = 2;
        else if (a == royalAuthority) { comboStep = 0; atonementStep = 1; divineMight = true; }
        else if (a == atonement) atonementStep = 2;
        else if (a == supplication) atonementStep = 3;
        else if (a == sepulchre) atonementStep = 0;
        else if (a == holySpirit) divineMight = false;
        else if (a == goringBlade) s.Remove("Goring Blade Ready");
        else if (a == imperator) requiescatStep = 1;
        else if (a == confiteor) requiescatStep = 2;
        else if (a == bladeOfFaith) requiescatStep = 3;
        else if (a == bladeOfTruth) requiescatStep = 4;
        else if (a == bladeOfValor) { requiescatStep = 0; honorReady = true; }
        else if (a == bladeOfHonor) honorReady = false;
    }
}
