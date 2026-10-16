using Rmc.Engine.Damage;

namespace Rmc.Engine.Sim.Rotations;

/// <summary>
/// Monk, level 100 (Dawntrail). Forms cycle opo-opo → raptor → coeurl, each form spending its Fury
/// (Leaping Opo, Rising Raptor, Pouncing Coeurl ×2) or building it (Dragon Kick, Twin Snakes,
/// Demolish). Riddle of Fire every minute with Perfect Balance (twice on even minutes) into the
/// blitzes, Lunar first ("double lunar"): Elixir Burst, Rising Phoenix, then Phantom Rush once both
/// Nadi are open. Fire's Reply and Wind's Reply under their Riddles, Brotherhood and the potion every
/// two minutes, The Forbidden Chakra at five Chakra.
/// Leaping Opo always crits (its opo-opo bonus holds under Perfect Balance and Formless Fist too,
/// as in xivgear's sim). Chakra counts as its expected value: the crit rate per weaponskill (one for a forced crit), one
/// per weaponskill under Meditative Brotherhood, and the party's 20% chances during it.
/// </summary>
public sealed class MnkRotation : Rotation
{
    /// <summary>Seven party members, a weaponskill or spell each per 2.5s, 20% each.</summary>
    private const double PartyChakraPerSecond = 7 * 0.2 / 2.5;

    public override string Job => "MNK";
    public override int TraitHastePercent => 20;

    private enum Form { Opo, Raptor, Coeurl }

    // GCDs
    private readonly SimAbility dragonKick = Gcd("Dragon Kick", 74, 320);
    private readonly SimAbility leapingOpo = Gcd("Leaping Opo", 36945, 460, "Opo-opo's Fury Potency", autoCrit: true);
    private readonly SimAbility twinSnakes = Gcd("Twin Snakes", 61, 420);
    private readonly SimAbility risingRaptor = Gcd("Rising Raptor", 36946, 540, "Raptor's Fury Potency");
    private readonly SimAbility demolish = Gcd("Demolish", 66, 420, "rear");
    private readonly SimAbility pouncingCoeurl = Gcd("Pouncing Coeurl", 36947, 520, "Flank Coeurl's Fury Potency");
    private readonly SimAbility elixirBurst = Gcd("Elixir Burst", 36948, 900);
    private readonly SimAbility risingPhoenix = Gcd("Rising Phoenix", 25768, 900);
    private readonly SimAbility phantomRush = Gcd("Phantom Rush", 25769, 1500);
    private readonly SimAbility firesReply = Gcd("Fire's Reply", 36950, 1400);
    private readonly SimAbility windsReply = Gcd("Wind's Reply", 36949, 1040);

    // oGCDs
    private readonly SimAbility perfectBalance = Ogcd("Perfect Balance", 69, 0, 40_000, charges: 2);
    private readonly SimAbility riddleOfFire = Ogcd("Riddle of Fire", 7395, 0, 60_000, grants: ["Riddle of Fire", "Fire's Rumination"]);
    private readonly SimAbility riddleOfWind = Ogcd("Riddle of Wind", 25766, 0, 90_000, grants: ["Riddle of Wind", "Wind's Rumination"]);
    private readonly SimAbility brotherhood = Ogcd("Brotherhood", 7396, 0, 120_000, grants: ["Brotherhood"]);
    private readonly SimAbility forbiddenChakra = Ogcd("The Forbidden Chakra", 3547, 400, 1000);
    private readonly SimAbility potion = Potion();

    private readonly List<SimAbility> abilities;
    private readonly List<SimBuff> buffs =
    [
        new() { Name = "Riddle of Fire", DurationMs = 20_000, DamagePercent = 15 },
        new() { Name = "Fire's Rumination", DurationMs = 20_000 },
        new() { Name = "Riddle of Wind", DurationMs = 15_000, AutoAttackHastePercent = 50 },
        new() { Name = "Wind's Rumination", DurationMs = 15_000 },
        new() { Name = "Brotherhood", DurationMs = 20_000, DamagePercent = 5 },
    ];

    private Form form;
    private bool formless;
    private int opoFury, raptorFury, coeurlFury;
    private int pbStacks;
    private readonly List<Form> beastChakra = [];
    private bool lunar, solar;
    private double chakra;
    private int brotherhoodAt = -1_000_000;

    public MnkRotation()
    {
        abilities =
        [
            dragonKick, leapingOpo, twinSnakes, risingRaptor, demolish, pouncingCoeurl, elixirBurst, risingPhoenix, phantomRush,
            firesReply, windsReply, perfectBalance, riddleOfFire, riddleOfWind, brotherhood, forbiddenChakra,
        ];
    }

    public override IReadOnlyList<SimAbility> Abilities => abilities;
    public override IReadOnlyList<SimBuff> Buffs => buffs;

    public override void Reset()
    {
        form = Form.Opo;
        formless = false;
        opoFury = raptorFury = coeurlFury = 0;
        pbStacks = 0;
        beastChakra.Clear();
        lunar = solar = false;
        chakra = 5; // Forbidden Meditation before the pull
        brotherhoodAt = -1_000_000;
    }

    private SimAbility ActionFor(Form f) => f switch
    {
        Form.Opo => opoFury > 0 ? leapingOpo : dragonKick,
        Form.Raptor => raptorFury > 0 ? risingRaptor : twinSnakes,
        _ => coeurlFury > 0 ? pouncingCoeurl : demolish,
    };

    public override SimAbility? NextGcd(SimState s)
    {
        if (beastChakra.Count == 3)
        {
            if (lunar && solar) return phantomRush;
            return beastChakra.Distinct().Count() == 1 ? elixirBurst : risingPhoenix;
        }
        if (pbStacks > 0)
        {
            // Lunar: three opo-opo actions; Solar: one of each form not used yet.
            var wantLunar = !lunar || (lunar && solar);
            if (wantLunar)
                return ActionFor(Form.Opo);
            var missing = new[] { Form.Opo, Form.Raptor, Form.Coeurl }.First(f => !beastChakra.Contains(f));
            return ActionFor(missing);
        }
        if (s.HasBuff("Fire's Rumination") && (s.BuffRemaining("Fire's Rumination") < 8000 || pbStacks == 0 && beastChakra.Count == 0))
            return firesReply;
        if (s.HasBuff("Wind's Rumination"))
            return windsReply;
        return ActionFor(form);
    }

    public override SimAbility? NextOgcd(SimState s)
    {
        if (chakra >= 5)
            return forbiddenChakra;
        if (s.IsReady(potion) && s.CooldownRemaining(brotherhood) < 3000 && s.Now > 2000 && s.Now + 15_000 < s.FightEndMs)
            return potion;
        if (s.IsReady(riddleOfFire) && s.Now > 2000)
            return riddleOfFire;
        if (s.IsReady(brotherhood) && s.HasBuff("Riddle of Fire"))
            return brotherhood;
        if (s.IsReady(riddleOfWind) && s.Now > 2000)
            return riddleOfWind;
        // Perfect Balance into Riddle of Fire: once a minute, twice when both charges are banked.
        if (s.IsReady(perfectBalance) && pbStacks == 0 && beastChakra.Count == 0 && !formless
            && (s.HasBuff("Riddle of Fire") || s.CooldownRemaining(riddleOfFire) < 5000)
            && (s.Charges(perfectBalance) == 2 || s.CooldownRemaining(perfectBalance) < s.CooldownRemaining(riddleOfFire) || !UsedPbThisWindow(s)))
            return perfectBalance;
        return null;
    }

    private int lastPbAt = -1_000_000;
    private bool UsedPbThisWindow(SimState s) => s.Now - lastPbAt < 25_000;

    public override void OnUse(SimAbility a, SimState s)
    {
        if (a.Gcd && a.Type == AttackType.Weaponskill)
        {
            var crit = a.AutoCrit ? 1.0 : s.CritRate;
            var inBrotherhood = s.Now - brotherhoodAt < 20_000;
            chakra = Math.Min(10, chakra + crit + (inBrotherhood ? 1 : 0) + (inBrotherhood ? PartyChakraPerSecond * s.GcdMs / 1000.0 : 0));
        }

        Form? usedForm = a == dragonKick || a == leapingOpo ? Form.Opo
            : a == twinSnakes || a == risingRaptor ? Form.Raptor
            : a == demolish || a == pouncingCoeurl ? Form.Coeurl : null;
        if (usedForm is { } f)
        {
            if (a == dragonKick) opoFury = 1;
            else if (a == leapingOpo) opoFury = 0;
            else if (a == twinSnakes) raptorFury = 1;
            else if (a == risingRaptor) raptorFury = 0;
            else if (a == demolish) coeurlFury = 2;
            else if (a == pouncingCoeurl) coeurlFury--;
            if (pbStacks > 0)
            {
                pbStacks--;
                beastChakra.Add(f);
            }
            formless = false;
            form = f switch { Form.Opo => Form.Raptor, Form.Raptor => Form.Coeurl, _ => Form.Opo };
        }
        else if (a == elixirBurst || a == risingPhoenix || a == phantomRush)
        {
            if (a == elixirBurst) lunar = true;
            else if (a == risingPhoenix) solar = true;
            else lunar = solar = false;
            beastChakra.Clear();
            formless = true;
            form = Form.Opo;
        }
        else if (a == firesReply)
        {
            s.Remove("Fire's Rumination");
            formless = true;
            form = Form.Opo;
        }
        else if (a == windsReply) s.Remove("Wind's Rumination");
        else if (a == perfectBalance) { pbStacks = 3; lastPbAt = s.Now; }
        else if (a == forbiddenChakra) chakra -= 5;
        else if (a == brotherhood) brotherhoodAt = s.Now;
    }
}
