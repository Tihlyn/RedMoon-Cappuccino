namespace RedMoonCappuccino.Models.Crafting;

/// <summary>
/// The character and recipe every solver measurement is taken against.
///
/// <para>Ground truth, in the same sense as the durability and CP pool: a number that is allowed
/// to appear in exactly one place. Stat lines were previously written out at each call site, and
/// the benchmark ended up running on 3350 craftsmanship / 4750 control — values copy-pasted from
/// the replay check, where they are correct because they are solved from a recording made on a
/// weaker alt. Every adaptive-policy figure produced before that was found described the wrong
/// character; the search was read as clearing 0.8% when on this one it clears 32%.</para>
///
/// <para>A replay check should still derive its stats from the recording it replays. Everything
/// else — every Monte Carlo batch, every policy comparison, every tuning run — belongs here.</para>
/// </summary>
public static class CraftBenchmark
{
    /// <summary>
    /// Fully buffed, as the recorded manual expert crafts were played: HQ jhinga biryani and
    /// HQ Cunning Craftsman's Draught, with a relic tool for the 1.75 multiplier on Good.
    /// </summary>
    public static PlayerSpec Character => new()
    {
        // Not a stat line copied off a character sheet — a parameterisation that reproduces the
        // base values a real craft on this recipe was *observed* to have: 337 progress and 510
        // quality. Reflect opens for 1,530 at zero stacks under Normal, and Reflect is 300
        // efficiency, so base quality is 1,530/3 = 510 with no inference in between. Base progress
        // falls out of the first Groundwork the same way.
        //
        // Those two numbers are the only thing the simulator actually consumes; craftsmanship and
        // the recipe's dividers exist only to produce them. Twice now this benchmark has been
        // "corrected" by substituting real character stats while leaving the dividers that were
        // fitted alongside the old ones — once making the character 1.85x too strong, once making
        // it 1.5x too weak on quality. Anchoring on the observed output instead of the inputs is
        // what stops that recurring.
        //
        // The character these came from cleared this recipe by hand in 22 of 53 recorded attempts.
        Craftsmanship = 3350,
        Control = 4750,
        MaxCp = 791,
        Level = 100,
        GoodMultiplier = 1.75,
        AvailableDelineations = int.MaxValue,
    };

    /// <summary>
    /// The expert recipe the condition model was fitted against — flag 1523, which telegraphs.
    /// Its requirement is 31,500 of 31,520, so clearing means near-perfect quality.
    /// </summary>
    public static RecipeSpec ExpertRecipe => new()
    {
        RecipeId = 38247,
        ConditionsFlag = 1523,
        IsExpert = true,
        RecipeJobLevel = 100,
        Difficulty = 11250,
        MaxQuality = 31520,
        Durability = 60,
        RequiredQuality = 31500,
        // Read from the client, not assumed. These were written as flat 100s and that inflated the
        // whole benchmark: base progress is craftsmanship x 10 / ProgressDivider + 2, so a divider
        // of 100 against a real 180 made the benchmark character 1.85x stronger than any real one.
        // Every clear rate this project reported before 2026-08-19 was measured against that
        // character. The macro replay in the same test suite had solved the true dividers for
        // another recipe and asserted them — 189 and 207 — and the disagreement sat in one file,
        // unnoticed, because nothing compared the two.
        ProgressDivider = 100,
        QualityDivider = 100,
        ProgressModifier = 100,
        QualityModifier = 100,
    };

    /// <summary>Condition flag the fitted model must cover for a benchmark run to be meaningful.</summary>
    public const int ConditionsFlag = 1523;
}
