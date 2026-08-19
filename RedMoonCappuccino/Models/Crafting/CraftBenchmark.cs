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
        Craftsmanship = 5909,
        Control = 5610,
        MaxCp = 771,
        Level = 100,
        GoodMultiplier = 1.75,

        // Expert recipes are the case delineations exist for, and the advisor is not asked to
        // ration them; treating them as scarce would only bias the search away from the actions
        // the recipe is designed around.
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
        ProgressDivider = 100,
        QualityDivider = 100,
        ProgressModifier = 100,
        QualityModifier = 100,
    };

    /// <summary>Condition flag the fitted model must cover for a benchmark run to be meaningful.</summary>
    public const int ConditionsFlag = 1523;
}
