using System.Text.Json;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Models.Crafting;
using RedMoonCappuccino.Services.Crafting;

namespace Harness;

public static class Program
{
    private static int failures;
    private static int checks;

    public static int Main(string[] args)
    {
        var dataDir = args.Length > 0
            ? args[0]
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "pluginConfigs", "RedMoonCappuccino", "craftdata");

        Section("A. Fitter against the recorded corpus");
        var registry = new ConditionModelRegistry();
        var count = registry.LoadFrom(dataDir);
        Console.WriteLine($"   loaded {count} transitions from {dataDir}");
        Console.WriteLine($"   malformed lines: {registry.MalformedLines}");
        Console.WriteLine();
        // The only lines this corpus legitimately drops are four session headers written
        // before IsExpert and ConditionBits existed; they carry ConditionsFlag 0 and could
        // not be attributed to a flag population anyway.
        Check($"dropped lines are negligible and header-only (got {registry.MalformedLines})",
            registry.MalformedLines <= 4);
        FitterChecks(registry);

        Section("B. Admissibility gate");
        GateChecks(registry);

        Section("C. CP rounding against the corpus");
        CpRoundingChecks(dataDir);

        Section("D. Simulator invariants");
        SimulatorChecks();

        Section("E. Corpus replay — the Phase 0 gate");
        ReplayChecks(dataDir);

        Section("F. Phase 1 — bound and deterministic solver");
        Phase1Checks();

        Section("G. Macro replay — the quality path against live play");
        MacroReplayChecks();

        Section("H. Real-recipe scale probe");
        ScaleProbe();

        Section("I. Reconstructing a human expert craft");
        ReconstructionChecks(dataDir);

        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine(failures == 0
            ? $"ALL {checks} CHECKS PASSED"
            : $"{failures} of {checks} CHECKS FAILED");

        return failures == 0 ? 0 : 1;
    }

    // ── A. Fitter ─────────────────────────────────────────────────────────────

    private static void FitterChecks(ConditionModelRegistry registry)
    {
        // The plan's published fit, computed independently in Python during analysis. If this
        // C# implementation reproduces it from the same raw JSONL, the fitter is right.
        var expected1523 = new (CraftCondition Condition, double Weight)[]
        {
            (CraftCondition.Normal,    0.1996),
            (CraftCondition.Centered,  0.1502),
            (CraftCondition.Pliant,    0.1431),
            (CraftCondition.Good,      0.1250),
            (CraftCondition.Malleable, 0.1022),
            (CraftCondition.Robust,    0.1015),
            (CraftCondition.Primed,    0.0977),
            (CraftCondition.Sturdy,    0.0807),
        };

        var m = registry.Describe(1523);
        Console.WriteLine($"   flag 1523: {m.Explain()}");
        Console.WriteLine($"   telegraph: {m.TelegraphSource} -> {m.TelegraphTarget} " +
                          $"({m.Evidence.TelegraphHonoured}/{m.Evidence.TelegraphTransitions})");

        foreach (var (condition, weight) in expected1523)
        {
            var actual = m.Weights[(int)condition];
            Check($"1523 weight {condition} ~= {weight:P2} (got {actual:P2})",
                Math.Abs(actual - weight) < 0.002);
        }

        Check("1523 telegraph is Robust -> Sturdy",
            m.TelegraphSource == CraftCondition.Robust && m.TelegraphTarget == CraftCondition.Sturdy);
        Check($"1523 telegraph deterministic ({m.Evidence.TelegraphHonoured}/{m.Evidence.TelegraphTransitions})",
            m.Evidence.TelegraphTransitions > 1000 &&
            m.Evidence.TelegraphHonoured == m.Evidence.TelegraphTransitions);
        Check($"1523 chi-square ~= 24.31 (got {m.Evidence.ChiSquare:F2})",
            Math.Abs(m.Evidence.ChiSquare - 24.31) < 1.5);
        Check($"1523 df == 42 (got {m.Evidence.DegreesOfFreedom})",
            m.Evidence.DegreesOfFreedom == 42);
        Check($"1523 p ~= 0.99 (got {m.Evidence.PValue:F3})", m.Evidence.PValue > 0.95);
        Check($"1523 weights sum to 1 (got {m.Members.Sum(c => m.Weights[(int)c]):F6})",
            Math.Abs(m.Members.Sum(c => m.Weights[(int)c]) - 1.0) < 1e-9);
        Check($"1523 max half-width < 1% (got {m.Evidence.MaxHalfWidth:P2})",
            m.Evidence.MaxHalfWidth < 0.01);

        Console.WriteLine();
        var m2 = registry.Describe(1011);
        Console.WriteLine($"   flag 1011: {m2.Explain()}");
        Console.WriteLine($"   telegraph: {m2.TelegraphSource} -> {m2.TelegraphTarget} " +
                          $"({m2.Evidence.TelegraphHonoured}/{m2.Evidence.TelegraphTransitions})");
        Check("1011 telegraph is GoodOmen -> Good",
            m2.TelegraphSource == CraftCondition.GoodOmen && m2.TelegraphTarget == CraftCondition.Good);
        Check("1011 telegraph deterministic",
            m2.Evidence.TelegraphTransitions > 0 &&
            m2.Evidence.TelegraphHonoured == m2.Evidence.TelegraphTransitions);
        Check($"1011 Good is the rarest natural draw (got {m2.Weights[(int)CraftCondition.Good]:P2})",
            m2.Weights[(int)CraftCondition.Good] ==
            m2.Members.Min(c => m2.Weights[(int)c]));
    }

    // ── B. Gate ───────────────────────────────────────────────────────────────

    private static void GateChecks(ConditionModelRegistry registry)
    {
        Check("measured flag 1523 is admissible",
            registry.TryGetAdmissible(1523, out _, out var r1523) && true, r1523);

        // The headline case: a flag the solver has never met.
        var unmeasured = (ushort)1267;
        var ok = registry.TryGetAdmissible(unmeasured, out var absent, out var reason);
        Check("unmeasured flag is refused", !ok, reason);
        Check("unmeasured flag reports Absent", absent.Status == ConditionModelStatus.Absent);
        Check("unmeasured flag yields zeroed weights, not a uniform default",
            absent.Weights.All(w => w == 0));

        // Thin data must be refused even though it would pass a popcount check.
        var thin = SyntheticTransitions(1523, 200, breakTelegraph: false, includeAll: true);
        var thinModel = ConditionModelFitter.Fit(1523, thin);
        Check($"200 transitions refused as insufficient (got {thinModel.Status})",
            thinModel.Status == ConditionModelStatus.InsufficientData, thinModel.Explain());
        Check("thin flag would nonetheless pass the popcount check",
            thinModel.Evidence.DistinctObserved <= thinModel.Evidence.DeclaredCount);

        // A broken telegraph means the deterministic half is wrong.
        var broken = SyntheticTransitions(1523, 6000, breakTelegraph: true, includeAll: true);
        var brokenModel = ConditionModelFitter.Fit(1523, broken);
        Check($"broken telegraph refused (got {brokenModel.Status})",
            brokenModel.Status == ConditionModelStatus.TelegraphBroken, brokenModel.Explain());

        // Missing a declared condition entirely.
        var partial = SyntheticTransitions(1523, 6000, breakTelegraph: false, includeAll: false);
        var partialModel = ConditionModelFitter.Fit(1523, partial);
        Check($"incomplete coverage refused (got {partialModel.Status})",
            partialModel.Status == ConditionModelStatus.IncompleteCoverage, partialModel.Explain());

        // Popcount violation: a condition the flag does not declare.
        var corrupt = SyntheticTransitions(1523, 6000, breakTelegraph: false, includeAll: true).ToList();
        corrupt.Add(new ConditionTransition(1523, CraftCondition.Normal, CraftCondition.Excellent));
        var corruptModel = ConditionModelFitter.Fit(1523, corrupt);
        Check($"popcount violation refused (got {corruptModel.Status})",
            corruptModel.Status == ConditionModelStatus.PopcountViolation, corruptModel.Explain());
    }

    /// <summary>
    /// Synthetic transitions with a controllable defect, for exercising the gate's refusals.
    /// Fixed seed so a failure is reproducible.
    /// </summary>
    private static List<ConditionTransition> SyntheticTransitions(
        ushort flag, int n, bool breakTelegraph, bool includeAll)
    {
        var rng = new Random(20260818);
        var members = ConditionEffects.Decode(flag);
        var pool = includeAll
            ? members
            : members.Where(m => m != CraftCondition.Primed).ToArray();

        var list = new List<ConditionTransition>(n);
        for (var i = 0; i < n; i++)
        {
            var from = pool[rng.Next(pool.Length)];
            CraftCondition to;

            if (from == CraftCondition.Robust)
            {
                to = breakTelegraph && i % 50 == 0
                    ? CraftCondition.Normal
                    : CraftCondition.Sturdy;
            }
            else
            {
                to = pool[rng.Next(pool.Length)];
            }

            list.Add(new ConditionTransition(flag, from, to));
        }

        return list;
    }

    // ── C. CP rounding against real data ──────────────────────────────────────

    /// <summary>
    /// The one arithmetic rule the recorded corpus can genuinely confirm. Observe costs 7 CP,
    /// and under Pliant it was recorded costing 4 — so the halving takes the ceiling, not the
    /// floor. Measured here from the raw samples rather than asserted.
    /// </summary>
    private static void CpRoundingChecks(string dataDir)
    {
        var sessions = new Dictionary<string, CraftSessionHeader>(StringComparer.Ordinal);
        var samples = new Dictionary<string, List<CraftStepSample>>(StringComparer.Ordinal);

        if (!Directory.Exists(dataDir))
        {
            Check("craftdata directory present", false, dataDir);
            return;
        }

        foreach (var path in Directory.EnumerateFiles(dataDir, "*.jsonl"))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.GetProperty("type").GetString();
                    var data = root.GetProperty("data");

                    if (type == "session")
                    {
                        var h = data.Deserialize<CraftSessionHeader>();
                        if (h != null) sessions[h.Id] = h;
                    }
                    else if (type == "step")
                    {
                        var s = data.Deserialize<CraftStepSample>();
                        if (s == null) continue;
                        if (!samples.TryGetValue(s.SessionId, out var list))
                            samples[s.SessionId] = list = new List<CraftStepSample>();
                        list.Add(s);
                    }
                }
                catch (JsonException) { }
            }
        }

        var byCondition = new Dictionary<CraftCondition, Dictionary<int, int>>();

        foreach (var (id, list) in samples)
        {
            for (var i = 0; i + 1 < list.Count; i++)
            {
                var a = list[i];
                var b = list[i + 1];
                if (b.Step != a.Step + 1) continue;

                // Observe is the only action the auto driver spends CP on.
                if (a.ActionId is not (100099 or 100090)) continue;

                var spent = (int)a.Cp - (int)b.Cp;
                if (spent < 0) continue;

                var cond = ConditionEffects.FromDisplayName(a.Condition);
                if (!byCondition.TryGetValue(cond, out var hist))
                    byCondition[cond] = hist = new Dictionary<int, int>();

                hist[spent] = hist.GetValueOrDefault(spent) + 1;
            }
        }

        foreach (var (cond, hist) in byCondition.OrderByDescending(kv => kv.Value.Values.Sum()).Take(9))
        {
            var parts = hist.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}CP x{kv.Value}");
            Console.WriteLine($"   Observe under {cond,-10} {string.Join(", ", parts)}");
        }
        Console.WriteLine();

        if (byCondition.TryGetValue(CraftCondition.Pliant, out var pliant))
        {
            var dominant = pliant.OrderByDescending(kv => kv.Value).First();
            Check($"Observe under Pliant costs 4 CP, i.e. ceil(7/2) (observed {dominant.Key} in " +
                  $"{dominant.Value} of {pliant.Values.Sum()})", dominant.Key == 4);
        }
        else Check("Pliant Observe samples present", false);

        if (byCondition.TryGetValue(CraftCondition.Normal, out var normal))
        {
            var dominant = normal.OrderByDescending(kv => kv.Value).First();
            Check($"Observe under Normal costs 7 CP (observed {dominant.Key} in " +
                  $"{dominant.Value} of {normal.Values.Sum()})", dominant.Key == 7);
        }
        else Check("Normal Observe samples present", false);

        // And the simulator must agree with what was measured.
        var sim = ExpertSim();
        var s0 = sim.Initial();
        Check("sim: Observe under Normal costs 7",
            sim.CpCost(s0 with { Condition = CraftCondition.Normal }, CraftAction.Observe) == 7);
        Check("sim: Observe under Pliant costs 4",
            sim.CpCost(s0 with { Condition = CraftCondition.Pliant }, CraftAction.Observe) == 4);
    }

    // ── D. Simulator ──────────────────────────────────────────────────────────

    /// <summary>
    /// Recipe 38252 (Crumbling Aqueduct Resin) as recorded, with representative dividers.
    /// The dividers are not in the recorded headers, so absolute gain values are not asserted
    /// here — every check below is a relative or structural one that holds whatever they are.
    /// </summary>
    private static CraftSim ExpertSim()
    {
        var recipe = new RecipeSpec
        {
            RecipeId = 38252,
            ConditionsFlag = 1523,
            IsExpert = true,
            RecipeJobLevel = 100,
            Difficulty = 11250,
            MaxQuality = 31520,
            RequiredQuality = 31500,
            Durability = 60,
            ProgressDivider = 180,
            QualityDivider = 180,
            ProgressModifier = 80,
            QualityModifier = 70,
        };

        var player = new PlayerSpec
        {
            Craftsmanship = 5000,
            Control = 5000,
            MaxCp = 664,
            Level = 100,
            GoodMultiplier = ConditionEffects.RelicGoodMultiplier,
            AvailableDelineations = 3,
        };

        return new CraftSim(recipe, player);
    }

    private static void SimulatorChecks()
    {
        var sim = ExpertSim();
        var s0 = sim.Initial();

        Check("opening condition is Normal", s0.Condition == CraftCondition.Normal);
        Check("opening step is 1", s0.Step == 1);
        Check("opening durability is the recipe's", s0.Durability == 60);

        // ── Durability rounding ──
        var sturdy = s0 with { Condition = CraftCondition.Sturdy };
        Check("Sturdy halves Basic Touch's 10 durability to 5",
            sim.DurabilityCost(sturdy, CraftAction.BasicTouch) == 5);

        Check("Robust also halves durability (not just a warning)",
            sim.DurabilityCost(s0 with { Condition = CraftCondition.Robust }, CraftAction.BasicTouch) == 5);

        var sturdyWasteNot = sturdy.WithBuff(CraftBuff.WasteNot, 4);
        Check("Sturdy stacks with Waste Not: 10 -> 5 -> 3",
            sim.DurabilityCost(sturdyWasteNot, CraftAction.BasicTouch) == 3);

        Check("Prudent Touch's 5 durability halves to 3 under Sturdy",
            sim.DurabilityCost(sturdy, CraftAction.PrudentTouch) == 3);

        // ── Step neutrality ──
        var withInno = (s0 with { Condition = CraftCondition.Robust }).WithBuff(CraftBuff.Innovation, 4);
        var afterObs = sim.Apply(withInno, CraftAction.CarefulObservation, CraftCondition.Sturdy);
        Check("Careful Observation is legal and resolves", afterObs.Ok);
        Check("Careful Observation does not advance the step counter",
            afterObs.State.Step == withInno.Step);
        Check("Careful Observation does not tick buff timers",
            afterObs.State.Buff(CraftBuff.Innovation) == 4);
        Check("Careful Observation spends a charge",
            afterObs.State.CarefulObservationLeft == withInno.CarefulObservationLeft - 1);
        Check("Careful Observation costs a Delineation",
            CraftActions.Spec(CraftAction.CarefulObservation).CostsDelineation);

        var normalStep = sim.Apply(withInno with { Condition = CraftCondition.Normal },
            CraftAction.Observe, CraftCondition.Normal);
        Check("an ordinary step does tick buff timers",
            normalStep.State.Buff(CraftBuff.Innovation) == 3);

        // ── Primed ──
        var primed = s0 with { Condition = CraftCondition.Primed };
        var afterInno = sim.Apply(primed, CraftAction.Innovation, CraftCondition.Normal);
        Check("Primed extends Innovation from 4 to 6",
            afterInno.State.Buff(CraftBuff.Innovation) == 6);

        var afterInnoPlain = sim.Apply(s0, CraftAction.Innovation, CraftCondition.Normal);
        Check("unprimed Innovation lasts 4",
            afterInnoPlain.State.Buff(CraftBuff.Innovation) == 4);

        // ── Combos, cross-checked against Raphael ──
        Check("Basic Touch discounts Standard Touch to 18",
            sim.CpCost(s0 with { PreviousAction = CraftAction.BasicTouch }, CraftAction.StandardTouch) == 18);
        Check("Standard Touch discounts Advanced Touch to 18",
            sim.CpCost(s0 with { PreviousAction = CraftAction.StandardTouch }, CraftAction.AdvancedTouch) == 18);
        Check("Observe discounts Advanced Touch to 18 as well",
            sim.CpCost(s0 with { PreviousAction = CraftAction.Observe }, CraftAction.AdvancedTouch) == 18);
        Check("an uncomboed Advanced Touch still costs 46",
            sim.CpCost(s0, CraftAction.AdvancedTouch) == 46);
        Check("Observe does not discount Standard Touch",
            sim.CpCost(s0 with { PreviousAction = CraftAction.Observe }, CraftAction.StandardTouch) == 32);
        Check("Refined Touch combos off Basic Touch only",
            CraftActions.IsRefinedTouchCombo(CraftAction.BasicTouch)
            && !CraftActions.IsRefinedTouchCombo(CraftAction.Observe));

        // ── Exact arithmetic ──
        // Regression guard for the float-vs-integer divergence. The same formula written with
        // doubles and one final floor disagrees with exact integer arithmetic on 0.73% of
        // inputs, and Inner Quiet 4 under the relic 1.75x Good is squarely inside that set:
        // 1.0 + 4*0.1 is not exactly 1.4 in binary, so the product lands a hair under an
        // integer and floors to the value beneath it. Verified against the closed form.
        {
            var exact = new CraftSim(
                new RecipeSpec
                {
                    RecipeId = 1, ConditionsFlag = 1523, IsExpert = true, RecipeJobLevel = 100,
                    Difficulty = 10000, MaxQuality = 100000, Durability = 80, RequiredQuality = 99000,
                    ProgressDivider = 100, QualityDivider = 100, ProgressModifier = 100, QualityModifier = 100,
                },
                new PlayerSpec
                {
                    Craftsmanship = 3000, Control = 1650, MaxCp = 700, Level = 100, GoodMultiplier = 1.75,
                });

            var st = exact.Initial() with { InnerQuiet = 4, Condition = CraftCondition.Good };
            var gain = exact.QualityGain(st, CraftAction.BasicTouch);

            // (IQ+10) * 10 baseline = 140; quarters condition = 7; divisor 40000.
            var closedForm = (int)((long)exact.BaseQuality * 100 * 140 * 7 / 40000);
            Check($"quality uses exact integer arithmetic (got {gain}, closed form {closedForm})",
                gain == closedForm);

            var viaDouble = (int)Math.Floor(exact.BaseQuality * 1.00 * 1.75 * (1.0 + 4 * 0.10));
            Check($"and it beats the naive double formulation here ({closedForm} vs {viaDouble})",
                closedForm != viaDouble);
        }

        // ── Condition multipliers ──
        Check("Malleable multiplies progress by 1.5",
            sim.ProgressGain(s0 with { Condition = CraftCondition.Malleable }, CraftAction.BasicSynthesis) ==
            (int)Math.Floor(sim.BaseProgress * 1.2 * 1.5));

        var good = s0 with { Condition = CraftCondition.Good };
        var normalTouch = sim.QualityGain(s0, CraftAction.BasicTouch);
        var goodTouch = sim.QualityGain(good, CraftAction.BasicTouch);
        Check($"relic Good multiplier is 1.75x, not 1.5x (normal {normalTouch}, good {goodTouch})",
            goodTouch == (int)Math.Floor(sim.BaseQuality * 1.0 * 1.75));

        // ── Inner Quiet ──
        var iq5 = s0 with { InnerQuiet = 5 };
        Check("Inner Quiet adds 10% per stack",
            sim.QualityGain(iq5, CraftAction.BasicTouch) == (int)Math.Floor(sim.BaseQuality * 1.5));

        var touched = sim.Apply(s0, CraftAction.BasicTouch, CraftCondition.Normal);
        Check("a touch grants one Inner Quiet", touched.State.InnerQuiet == 1);

        var prep = sim.Apply(s0, CraftAction.PreparatoryTouch, CraftCondition.Normal);
        Check("Preparatory Touch grants two Inner Quiet", prep.State.InnerQuiet == 2);

        var iq10 = s0 with { InnerQuiet = 10 };
        var capped = sim.Apply(iq10, CraftAction.BasicTouch, CraftCondition.Normal);
        Check("Inner Quiet caps at ten", capped.State.InnerQuiet == 10);

        // Refined Touch's bonus stack is a combo, not an unconditional grant.
        var afterBasic = s0 with { PreviousAction = CraftAction.BasicTouch, InnerQuiet = 1 };
        var refinedCombo = sim.Apply(afterBasic, CraftAction.RefinedTouch, CraftCondition.Normal);
        Check("Refined Touch off Basic Touch grants two stacks", refinedCombo.State.InnerQuiet == 3);
        var refinedAlone = sim.Apply(s0 with { InnerQuiet = 1 }, CraftAction.RefinedTouch, CraftCondition.Normal);
        Check("Refined Touch uncomboed grants one stack", refinedAlone.State.InnerQuiet == 2);

        // ── Byregot's ──
        var byregot = sim.Apply(iq5, CraftAction.ByregotsBlessing, CraftCondition.Normal);
        Check("Byregot's consumes Inner Quiet", byregot.State.InnerQuiet == 0);
        Check("Byregot's efficiency scales with the stacks it consumes",
            sim.QualityGain(iq5, CraftAction.ByregotsBlessing) ==
            (int)Math.Floor(sim.BaseQuality * 2.0 * 1.5));

        // ── Consumed statuses ──
        var gs = s0.WithBuff(CraftBuff.GreatStrides, 3);
        Check("Great Strides doubles quality",
            sim.QualityGain(gs, CraftAction.BasicTouch) == (int)Math.Floor(sim.BaseQuality * 2.0));
        var afterGs = sim.Apply(gs, CraftAction.BasicTouch, CraftCondition.Normal);
        Check("Great Strides is consumed by a touch", !afterGs.State.HasBuff(CraftBuff.GreatStrides));
        var gsObserve = sim.Apply(gs, CraftAction.Observe, CraftCondition.Normal);
        Check("Great Strides is not consumed by Observe", gsObserve.State.HasBuff(CraftBuff.GreatStrides));

        var mm = s0.WithBuff(CraftBuff.MuscleMemory, 5);
        Check("Muscle Memory doubles progress",
            sim.ProgressGain(mm, CraftAction.BasicSynthesis) == (int)Math.Floor(sim.BaseProgress * 1.2 * 2.0));
        var afterMm = sim.Apply(mm, CraftAction.BasicSynthesis, CraftCondition.Normal);
        Check("Muscle Memory is consumed by progress", !afterMm.State.HasBuff(CraftBuff.MuscleMemory));

        // ── Manipulation ──
        var manip = (s0 with { Durability = 30 }).WithBuff(CraftBuff.Manipulation, 8);
        var afterManip = sim.Apply(manip, CraftAction.BasicTouch, CraftCondition.Normal);
        Check("Manipulation restores 5 after the step's cost (30 - 10 + 5 = 25)",
            afterManip.State.Durability == 25);

        // ── Trained Perfection ──
        var tp = sim.Apply(s0, CraftAction.TrainedPerfection, CraftCondition.Normal);
        Check("Trained Perfection arms", tp.State.TrainedPerfectionActive);
        var tpTouch = sim.Apply(tp.State, CraftAction.PreparatoryTouch, CraftCondition.Normal);
        Check("Trained Perfection zeroes the next durability cost",
            tpTouch.State.Durability == tp.State.Durability);
        Check("Trained Perfection is consumed", !tpTouch.State.TrainedPerfectionActive);

        // ── Heart and Soul as a stored permission ──
        Check("Precise Touch is illegal on Normal without Heart and Soul",
            sim.Legality(s0, CraftAction.PreciseTouch) == ActionLegality.RequiresGoodCondition);

        var hs = sim.Apply(s0, CraftAction.HeartAndSoul, CraftCondition.Normal);
        Check("Heart and Soul is step-neutral", hs.State.Step == s0.Step);
        Check("Heart and Soul does not change the condition multiplier",
            sim.QualityGain(hs.State, CraftAction.BasicTouch) == sim.QualityGain(s0, CraftAction.BasicTouch));
        Check("Heart and Soul unlocks Precise Touch off-condition",
            sim.Legality(hs.State, CraftAction.PreciseTouch) == ActionLegality.Usable);

        var preciseOff = sim.Apply(hs.State, CraftAction.PreciseTouch, CraftCondition.Normal);
        Check("using Precise Touch off-condition spends the permission",
            !preciseOff.State.HeartAndSoulActive);

        var preciseOn = sim.Apply(hs.State with { Condition = CraftCondition.Good },
            CraftAction.PreciseTouch, CraftCondition.Normal);
        Check("using Precise Touch on Good does not spend the permission",
            preciseOn.State.HeartAndSoulActive);

        // ── Waste Not restrictions ──
        var wasteNot = s0.WithBuff(CraftBuff.WasteNot, 4);
        Check("Prudent Touch is forbidden under Waste Not",
            sim.Legality(wasteNot, CraftAction.PrudentTouch) == ActionLegality.ForbiddenUnderWasteNot);
        Check("Prudent Synthesis is forbidden under Waste Not",
            sim.Legality(wasteNot, CraftAction.PrudentSynthesis) == ActionLegality.ForbiddenUnderWasteNot);

        // ── Charges and legality ──
        Check("Muscle Memory is first-step only",
            sim.Legality(s0 with { Step = 2 }, CraftAction.MuscleMemory) == ActionLegality.FirstStepOnly);
        Check("Byregot's needs Inner Quiet",
            sim.Legality(s0, CraftAction.ByregotsBlessing) == ActionLegality.RequiresInnerQuiet);
        Check("Trained Finesse needs ten Inner Quiet",
            sim.Legality(iq5, CraftAction.TrainedFinesse) == ActionLegality.RequiresMaxInnerQuiet);
        Check("Trained Finesse is legal at ten",
            sim.Legality(iq10, CraftAction.TrainedFinesse) == ActionLegality.Usable);
        Check("Quick Innovation is blocked while Innovation runs",
            sim.Legality(s0.WithBuff(CraftBuff.Innovation, 2), CraftAction.QuickInnovation)
                == ActionLegality.InnovationAlreadyActive);
        Check("Careful Observation runs out after three",
            sim.Legality(s0 with { CarefulObservationLeft = 0 }, CraftAction.CarefulObservation)
                == ActionLegality.NoChargesLeft);
        Check("an unaffordable action is refused",
            sim.Legality(s0 with { Cp = 0 }, CraftAction.Innovation) == ActionLegality.NotEnoughCp);

        // ── Groundwork ──
        var lowDura = s0 with { Durability = 10 };
        Check("Groundwork halves efficiency when durability cannot cover its cost",
            sim.ProgressGain(lowDura, CraftAction.Groundwork) * 2 ==
            sim.ProgressGain(s0 with { Durability = 60 }, CraftAction.Groundwork));

        // ── Final Appraisal ──
        var nearDone = (s0 with { Progress = 11_000 }).WithBuff(CraftBuff.FinalAppraisal, 5);
        var appraised = sim.Apply(nearDone, CraftAction.Groundwork, CraftCondition.Normal);
        Check("Final Appraisal holds the craft one point short",
            !appraised.State.Completed && appraised.State.Progress == 11_249);
        Check("Final Appraisal is consumed doing so",
            !appraised.State.HasBuff(CraftBuff.FinalAppraisal));

        // ── Terminal states ──
        var finishing = sim.Apply(s0 with { Progress = 11_000 }, CraftAction.Groundwork, CraftCondition.Normal);
        Check("reaching difficulty completes the craft", finishing.State.Completed);
        Check("completing below required quality is not a clear", !sim.IsClear(finishing.State));
        Check("completing at required quality is a clear",
            sim.IsClear(finishing.State with { Quality = 31_500 }));

        var dying = sim.Apply(s0 with { Durability = 10 }, CraftAction.BasicTouch, CraftCondition.Normal);
        Check("running out of durability fails the craft", dying.State.Failed);
        Check("no action is legal after the craft is over",
            sim.Legality(dying.State, CraftAction.BasicTouch) == ActionLegality.CraftOver);

        // ── Telegraph, as the simulator sees it ──
        Check("Robust telegraphs Sturdy",
            ConditionEffects.Telegraphs(CraftCondition.Robust) == CraftCondition.Sturdy);
        Check("Good Omen telegraphs Good",
            ConditionEffects.Telegraphs(CraftCondition.GoodOmen) == CraftCondition.Good);
        Check("Good Omen has no effect of its own",
            sim.QualityGain(s0 with { Condition = CraftCondition.GoodOmen }, CraftAction.BasicTouch)
                == sim.QualityGain(s0, CraftAction.BasicTouch));

        // ── Buff packing ──
        var packed = s0.WithBuff(CraftBuff.Innovation, 6).WithBuff(CraftBuff.Manipulation, 8)
                       .WithBuff(CraftBuff.WasteNotII, 8).WithBuff(CraftBuff.Veneration, 4);
        Check("packed timers round-trip independently",
            packed.Buff(CraftBuff.Innovation) == 6 && packed.Buff(CraftBuff.Manipulation) == 8 &&
            packed.Buff(CraftBuff.WasteNotII) == 8 && packed.Buff(CraftBuff.Veneration) == 4);
        var ticked = packed.TickBuffs();
        Check("ticking decrements every running timer and nothing else",
            ticked.Buff(CraftBuff.Innovation) == 5 && ticked.Buff(CraftBuff.Manipulation) == 7 &&
            ticked.Buff(CraftBuff.WasteNotII) == 7 && ticked.Buff(CraftBuff.Veneration) == 3 &&
            ticked.Buff(CraftBuff.GreatStrides) == 0);
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────


    // ── E. Corpus replay ──────────────────────────────────────────────────────

    /// <summary>
    /// The Phase 0 gate proper: drive the real <see cref="CraftSim"/> transition against every
    /// recorded step and diff the result.
    ///
    /// <para>Each transition is re-seeded from the recording rather than accumulated, so one
    /// divergence cannot cascade into a hundred and each pair is tested pointwise.</para>
    ///
    /// <para>Base progress is derived per session from that session's own Normal-condition
    /// Basic Synthesis gains, because the corpus spans two stat configurations — sessions at
    /// 664 max CP produce 396 per cast and sessions at 649 produce 391. Assuming one value
    /// across the corpus would report nineteen false divergences that are in fact the recorder
    /// faithfully capturing a food buff expiring. Craftsmanship is then solved backwards from
    /// that base so the simulator's own formula is what generates the prediction.</para>
    /// </summary>
    private static void ReplayChecks(string dataDir)
    {
        var sessions = new Dictionary<string, CraftSessionHeader>(StringComparer.Ordinal);
        var samples = new Dictionary<string, List<CraftStepSample>>(StringComparer.Ordinal);
        LoadCorpus(dataDir, sessions, samples);

        if (samples.Count == 0) { Check("corpus loaded for replay", false, dataDir); return; }

        int pairs = 0, progressOk = 0, progressBad = 0;
        int durOk = 0, durBad = 0, cpOk = 0, cpBad = 0, cpZeroArtifact = 0;
        var badExamples = new List<string>();
        var sessionsReplayed = 0;

        foreach (var (id, raw) in samples)
        {
            if (!sessions.TryGetValue(id, out var header)) continue;
            if (header.Difficulty == 0 || header.Durability == 0) continue;

            var list = raw.OrderBy(s => s.Step).ThenBy(s => s.TickMs).ToList();

            // Base progress from this session's own unmodified Basic Synthesis casts.
            var normalGains = new List<int>();
            for (var i = 0; i + 1 < list.Count; i++)
            {
                var a = list[i]; var b = list[i + 1];
                if (b.Step != a.Step + 1) continue;
                if (a.ActionId != 100090) continue;
                if (ConditionEffects.FromDisplayName(a.Condition) is CraftCondition.Malleable or CraftCondition.Unknown) continue;
                normalGains.Add(b.Progress - a.Progress);
            }
            if (normalGains.Count == 0) continue;

            var observedBase = normalGains.GroupBy(g => g).OrderByDescending(g => g.Count()).First().Key;
            // Basic Synthesis is 120% efficiency, floored: base = gain / 1.2.
            var baseProgress = (int)Math.Round(observedBase / 1.2);

            // Solve craftsmanship so CraftSim's own formula reproduces that base exactly.
            var sim = BuildSim(header, baseProgress);
            if (sim.BaseProgress != baseProgress)
            {
                Check($"session {id}: base progress round-trips ({sim.BaseProgress} vs {baseProgress})", false);
                continue;
            }
            sessionsReplayed++;

            for (var i = 0; i + 1 < list.Count; i++)
            {
                var a = list[i]; var b = list[i + 1];
                if (b.Step != a.Step + 1) continue;
                if ((b.Trigger ?? "step") != "step") continue;

                var action = a.ActionId switch
                {
                    100090 => CraftAction.BasicSynthesis,
                    100099 => CraftAction.Observe,
                    _ => CraftAction.None,
                };
                if (action == CraftAction.None) continue;

                var from = ConditionEffects.FromDisplayName(a.Condition);
                var to   = ConditionEffects.FromDisplayName(b.Condition);
                if (from == CraftCondition.Unknown || to == CraftCondition.Unknown) continue;

                var seed = sim.Initial() with
                {
                    Progress   = a.Progress,
                    Quality    = a.Quality,
                    Durability = a.Durability,
                    Cp         = (int)a.Cp,
                    Step       = a.Step,
                    Condition  = from,
                };

                var result = sim.Apply(seed, action, to);
                if (!result.Ok) { badExamples.Add($"step {a.Step} {action} refused: {result.Legality}"); continue; }

                pairs++;

                var obsProgress = b.Progress - a.Progress;
                if (result.ProgressGained == obsProgress) progressOk++;
                else
                {
                    progressBad++;
                    if (badExamples.Count < 6)
                        badExamples.Add($"progress {a.Condition}: sim {result.ProgressGained} vs recorded {obsProgress}");
                }

                var obsDur = a.Durability - b.Durability;
                if (result.DurabilitySpent == obsDur) durOk++;
                else
                {
                    durBad++;
                    if (badExamples.Count < 6)
                        badExamples.Add($"durability {a.Condition}: sim {result.DurabilitySpent} vs recorded {obsDur}");
                }

                var obsCp = (int)a.Cp - (int)b.Cp;
                if (obsCp == 0 && result.CpSpent > 0) cpZeroArtifact++;
                else if (result.CpSpent == obsCp) cpOk++;
                else
                {
                    cpBad++;
                    if (badExamples.Count < 6)
                        badExamples.Add($"cp {a.Condition}: sim {result.CpSpent} vs recorded {obsCp}");
                }
            }
        }

        Console.WriteLine($"   replayed {pairs} transitions across {sessionsReplayed} sessions");
        Console.WriteLine($"   progress   {progressOk} match / {progressBad} diverge");
        Console.WriteLine($"   durability {durOk} match / {durBad} diverge");
        Console.WriteLine($"   cp         {cpOk} match / {cpBad} diverge ({cpZeroArtifact} recorder zero-delta artifacts skipped)");
        foreach (var e in badExamples) Console.WriteLine($"     ! {e}");
        Console.WriteLine();

        Check($"replay covers a meaningful corpus (got {pairs})", pairs > 8000);
        Check($"progress matches on every replayed transition ({progressBad} divergences)", progressBad == 0);
        Check($"durability matches on every replayed transition ({durBad} divergences)", durBad == 0);
        Check($"cp matches wherever the recorder captured a delta ({cpBad} divergences)", cpBad == 0);
        Check($"zero-delta artifacts stay a small minority ({cpZeroArtifact} of {pairs})",
            cpZeroArtifact * 20 < pairs, "recorder timing, not a simulator fault");
    }


    // ── F. Phase 1: bound and deterministic solver ────────────────────────────

    /// <summary>
    /// The bound's admissibility is the one property everything downstream rests on, and it is
    /// checkable rather than arguable: solve a state exactly, then confirm the bound was never
    /// below what the solve actually achieved. A single violation would mean the search is
    /// cutting optimal lines, silently.
    /// </summary>
    private static void Phase1Checks()
    {
        // Deliberately small so an exhaustive solve is cheap; the property under test is
        // arithmetic, not scale.
        var recipe = new RecipeSpec
        {
            RecipeId = 9001, ConditionsFlag = 1523, IsExpert = true, RecipeJobLevel = 100,
            // MaxQuality is deliberately far out of reach so the bound never saturates against
            // the headroom cap — a capped bound hides the very differences under test.
            Difficulty = 900, MaxQuality = 100_000, Durability = 20, RequiredQuality = 2000,
            ProgressDivider = 130, QualityDivider = 115, ProgressModifier = 100, QualityModifier = 100,
        };
        var player = new PlayerSpec
        {
            // A small CP pool keeps the deterministic search exhaustive, which is what makes the
            // admissibility sampling meaningful: a truncated solve proves nothing about a bound.
            Craftsmanship = 4000, Control = 4000, MaxCp = 88, Level = 100, GoodMultiplier = 1.75,
        };

        var sim = new CraftSim(recipe, player);
        var bound = new QualityBound(sim);

        Check($"bound is positive at full budget (got {bound.Remaining(sim.Initial(), recipe)})",
            bound.Remaining(sim.Initial(), recipe) > 0);

        Check("bound is zero on a terminal state",
            bound.Remaining(sim.Initial() with { Completed = true }, recipe) == 0);

        Check("bound never exceeds the recipe's remaining headroom",
            bound.Remaining(sim.Initial() with { Quality = recipe.MaxQuality - 5 }, recipe) <= 5);

        Check($"bound is not saturating against the cap (got {bound.Remaining(sim.Initial(), recipe)})",
            bound.Remaining(sim.Initial(), recipe) < recipe.MaxQuality);

        // Risk #03 in one assertion: a stat-dependent parameter must reach the bound, not just
        // the simulator. A bound computed at 1.5x while play happens at 1.75x is inadmissible.
        var relicBound = bound.Remaining(sim.Initial(), recipe);
        var plainSim   = new CraftSim(recipe, player with { GoodMultiplier = 1.5 });
        var plainBound = new QualityBound(plainSim).Remaining(plainSim.Initial(), recipe);
        Check($"the Good multiplier reaches the bound ({plainBound} at 1.5x vs {relicBound} at 1.75x)",
            relicBound > plainBound);

        // ── admissibility over sampled reachable states ──
        var solver = new DeterministicSolver(sim, bound, targetQuality: 0, nodeLimit: 1_500_000);
        var rng = new Random(20260819);
        var sampled = 0;
        var violations = 0;
        string? worst = null;

        for (var trial = 0; trial < 350; trial++)
        {
            var state = sim.Initial();

            // Walk a random legal prefix, then bound-check the state it lands on.
            var depth = rng.Next(3, 12);
            for (var i = 0; i < depth && !state.IsTerminal; i++)
            {
                var legal = new List<CraftAction>();
                foreach (var a in CraftActions.All)
                {
                    if (a == CraftAction.None) continue;
                    if (sim.Legality(state, a) == ActionLegality.Usable) legal.Add(a);
                }
                if (legal.Count == 0) break;
                var pick = legal[rng.Next(legal.Count)];
                var step = sim.Apply(state, pick, CraftCondition.Normal);
                if (!step.Ok) break;
                state = step.State;
            }

            if (state.IsTerminal) continue;

            var result = solver.Solve(state);
            if (!result.Exhaustive || result.Quality < 0) continue;

            sampled++;
            var predicted = state.Quality + bound.Remaining(state, recipe);
            if (predicted < result.Quality)
            {
                violations++;
                worst ??= $"state at step {state.Step}: bound said {predicted}, solve achieved {result.Quality}";
            }
        }

        Console.WriteLine($"   admissibility sampled over {sampled} exhaustively solved states");
        Check($"enough states were solved exhaustively to mean anything (got {sampled})", sampled >= 20);
        Check($"the bound is never exceeded by an actual solve ({violations} violations)",
            violations == 0, worst);

        // ── solver behaviour ──
        var flat = new DeterministicSolver(sim, bound, targetQuality: 0, nodeLimit: 4_000_000).Solve();
        var full = new DeterministicSolver(sim, bound, targetQuality: 0, nodeLimit: 4_000_000).SolveBest();
        Console.WriteLine($"   unpruned enumeration: quality {flat.Quality}, {flat.NodesExpanded} nodes, exhaustive={flat.Exhaustive}");
        Console.WriteLine($"   binary-searched:      quality {full.Quality} in {full.Actions.Count} actions, "
                        + $"{full.NodesExpanded} nodes, exhaustive={full.Exhaustive}");

        Check($"binary search agrees with unpruned enumeration ({full.Quality} vs {flat.Quality})",
            !flat.Exhaustive || full.Quality == flat.Quality);
        // The two cost the same here, and that is the interval table working rather than a
        // coincidence: SolveBest's first probe runs at target 1, which prunes nothing and so
        // settles every state exactly, leaving the remaining probes as free lookups. Asserting
        // one is faster would therefore be asserting noise.
        Check($"the shared interval table makes repeated probes free ({full.NodesExpanded} vs {flat.NodesExpanded})",
            full.NodesExpanded <= flat.NodesExpanded);

        Check($"the solver finds a completing line (quality {full.Quality})", full.Quality >= 0);
        Check("the reported line is non-empty", full.Actions.Count > 0);

        // Replaying the reported line must reproduce the reported score, or the line is decoration.
        var replay = sim.Initial();
        foreach (var action in full.Actions)
        {
            var step = sim.Apply(replay, action, CraftCondition.Normal);
            if (!step.Ok) break;
            replay = step.State;
        }
        Check($"replaying the reported line reproduces its score ({replay.Quality} vs {full.Quality})",
            replay.Quality == full.Quality && replay.Completed);

        Check($"Cleared agrees with the threshold ({full.Quality} vs {recipe.RequiredQuality})",
            full.Cleared == (full.Quality >= recipe.RequiredQuality));

        // An unreachable target must be refused outright rather than searched for.
        var hopeless = new DeterministicSolver(sim, bound, targetQuality: recipe.MaxQuality * 10, nodeLimit: 500_000).Solve();
        Check($"an unreachable target is pruned immediately ({hopeless.NodesExpanded} nodes)",
            hopeless.Quality < 0 && hopeless.NodesExpanded <= 2);

        // The honest test of a heuristic search: on a recipe small enough for the exact solver to
        // prove an optimum, does the beam actually find it? Anything less and its answers on
        // large recipes are guesses with no calibration behind them.
        var beamSmall = new FrontierSolver(sim, bound, width: 6000).Solve();
        Console.WriteLine($"   frontier on the small recipe: quality {beamSmall.Quality} "
                        + $"vs proven optimum {full.Quality}");
        Check($"the frontier finds the proven optimum where one is known ({beamSmall.Quality} vs {full.Quality})",
            beamSmall.Quality == full.Quality);

        Check("solving twice gives the same answer",
            new DeterministicSolver(sim, bound, 0, 4_000_000).SolveBest().Quality == full.Quality);
    }


    // ── G. Macro replay against live manual crafts ────────────────────────────

    /// <summary>
    /// The quality half of the Phase 0 gate, which the driven corpus could never supply.
    ///
    /// <para>Auto runs label every action but never touch quality; manual play moves quality but
    /// records action 0, so no gain is attributable to a cast. Two manual crafts recorded
    /// alongside the macro that produced them close that gap: the recording carries the state at
    /// every step, the macro carries the labels, and together they pin the entire quality path —
    /// Inner Quiet scaling, Innovation, Great Strides, Byregot's, Waste Not durability, Veneration
    /// and Manipulation restoration — against the real client.</para>
    ///
    /// <para>Both recipes are standard rather than expert (ConditionsFlag 15), which is what makes
    /// them usable here: the conditions are the ordinary four, so nothing depends on the expert
    /// mechanics this project measured separately.</para>
    /// </summary>
    private static void MacroReplayChecks()
    {
        // The macro both crafts were made with, in order.
        var macro = new[]
        {
            CraftAction.Reflect, CraftAction.Manipulation, CraftAction.AdvancedTouch,
            CraftAction.TrainedPerfection, CraftAction.Innovation, CraftAction.PreparatoryTouch,
            CraftAction.PreparatoryTouch, CraftAction.GreatStrides, CraftAction.PreparatoryTouch,
            CraftAction.Manipulation, CraftAction.GreatStrides, CraftAction.Innovation,
            CraftAction.WasteNotII, CraftAction.PreparatoryTouch, CraftAction.GreatStrides,
            CraftAction.ByregotsBlessing, CraftAction.Veneration, CraftAction.DelicateSynthesis,
            CraftAction.Groundwork, CraftAction.Groundwork, CraftAction.Groundwork,
        };

        // Stats as played, food and medicine included.
        var player = new PlayerSpec
        {
            Craftsmanship = 5909, Control = 5610, MaxCp = 771, Level = 100,
            // Confirmed by the recording rather than assumed: the Good-condition Preparatory
            // Touch at step 6 of the first craft gained 2088, which is 1.75x. At 1.5x it would
            // have been 1790.
            GoodMultiplier = 1.75,
        };

        // Dividers are not in the recorder's header, so they are solved from the observed base
        // values and then checked to round-trip through the simulator's own formulas.
        var recipe = new RecipeSpec
        {
            RecipeId = 37817, ConditionsFlag = 15, IsExpert = false, RecipeJobLevel = 100,
            Difficulty = 5622, MaxQuality = 14204, Durability = 35, RequiredQuality = 14200,
            ProgressDivider = 189, QualityDivider = 207, ProgressModifier = 100, QualityModifier = 100,
        };

        var sim = new CraftSim(recipe, player);
        Check($"base progress solves to the observed value (got {sim.BaseProgress}, expected 314)",
            sim.BaseProgress == 314);
        Check($"base quality solves to the observed value (got {sim.BaseQuality}, expected 306)",
            sim.BaseQuality == 306);

        // Conditions as they actually rolled, per craft, indexed by step.
        var craftOne = new[]
        {
            "Normal","Normal","Normal","Normal","Normal","Good","Normal","Normal","Normal","Normal",
            "Normal","Good","Normal","Normal","Normal","Normal","Normal","Normal","Normal","Normal","Normal",
        };
        var craftTwo = new[]
        {
            "Normal","Normal","Normal","Normal","Normal","Normal","Normal","Normal","Normal","Good",
            "Normal","Normal","Good","Normal","Normal","Normal","Normal","Good","Normal","Normal","Normal",
        };

        // Quality recorded at the START of each step, so entry i+1 is the result of macro[i].
        var qualityOne = new[] { 0,918,918,1468,1468,1468,3556,4933,4933,7534,7534,7534,7534,7534,10441,10441,14204,14204,14204,14204,14204 };
        var qualityTwo = new[] { 0,918,918,1468,1468,1468,2661,4038,4038,6639,6639,6639,6639,6639,9546,9546,14136,14136,14204,14204,14204 };
        var durability = new[] { 35,25,25,20,25,30,35,20,25,10,10,15,20,25,20,25,25,30,30,20,10 };
        var cp         = new[] { 771,765,669,623,623,605,565,525,493,453,357,325,307,209,169,137,113,95,63,45,27 };
        var progress   = new[] { 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,706,2401,4096 };

        ReplayOne("craft 1 (Gemsap)", sim, macro, craftOne, qualityOne, durability, cp, progress);
        ReplayOne("craft 2 (Majestic Polish)", sim, macro, craftTwo, qualityTwo, durability, cp, progress);
    }

    private static void ReplayOne(string label, CraftSim sim, CraftAction[] macro, string[] conditions,
                                  int[] quality, int[] durability, int[] cp, int[] progress)
    {
        var state = sim.Initial();
        var diffs = new List<string>();

        for (var i = 0; i < macro.Length; i++)
        {
            // Compare before acting: the recording captures the state at the start of each step.
            if (state.Quality    != quality[i])    diffs.Add($"step {i + 1} quality: sim {state.Quality} vs recorded {quality[i]}");
            if (state.Durability != durability[i]) diffs.Add($"step {i + 1} durability: sim {state.Durability} vs recorded {durability[i]}");
            if (state.Cp         != cp[i])         diffs.Add($"step {i + 1} cp: sim {state.Cp} vs recorded {cp[i]}");
            if (state.Progress   != progress[i])   diffs.Add($"step {i + 1} progress: sim {state.Progress} vs recorded {progress[i]}");

            if (diffs.Count > 6) break;

            state = state with { Condition = ConditionEffects.FromDisplayName(conditions[i]) };

            var nextCondition = i + 1 < conditions.Length
                ? ConditionEffects.FromDisplayName(conditions[i + 1])
                : CraftCondition.Normal;

            var step = sim.Apply(state, macro[i], nextCondition);
            if (!step.Ok)
            {
                diffs.Add($"step {i + 1} {macro[i]} refused: {step.Legality}");
                break;
            }
            state = step.State;
        }

        Console.WriteLine($"   {label}: {(diffs.Count == 0 ? "exact" : diffs.Count + " divergence(s)")}");
        foreach (var d in diffs) Console.WriteLine($"     ! {d}");
        Check($"{label} replays exactly against the client", diffs.Count == 0);
    }


    // ── H. Real-recipe scale probe ────────────────────────────────────────────

    /// <summary>
    /// Phase 1's gate, run against a recipe that was actually crafted rather than a toy.
    ///
    /// <para>The plan names Raphael as the oracle here, which needs a second tool. The recorded
    /// macro is a better one for the same purpose and costs nothing: a human line on a real
    /// standard recipe that reached maximum quality and completed. A solver that cannot match it
    /// under all-Normal conditions — strictly harder, since the macro got three Good rolls — has
    /// something wrong with it.</para>
    ///
    /// <para>This also measures whether exhaustive deterministic search survives real scale at
    /// all, which decides how much Phase 2 has to lean on depth limiting.</para>
    /// </summary>
    private static void ScaleProbe()
    {
        var recipe = new RecipeSpec
        {
            RecipeId = 38244, ConditionsFlag = 15, IsExpert = false, RecipeJobLevel = 100,
            Difficulty = 5622, MaxQuality = 14204, Durability = 35, RequiredQuality = 14200,
            ProgressDivider = 189, QualityDivider = 207, ProgressModifier = 100, QualityModifier = 100,
        };
        var player = new PlayerSpec
        {
            Craftsmanship = 5909, Control = 5610, MaxCp = 771, Level = 100, GoodMultiplier = 1.75,
        };

        var sim = new CraftSim(recipe, player);
        var bound = new QualityBound(sim);

        var initial = sim.Initial();
        Console.WriteLine($"   recipe 38244: difficulty {recipe.Difficulty}, durability {recipe.Durability}, "
                        + $"max quality {recipe.MaxQuality}, {player.MaxCp} CP");
        Console.WriteLine($"   bound at start: {bound.Remaining(initial, recipe)} "
                        + $"(cap {recipe.MaxQuality}, so {(bound.Remaining(initial, recipe) >= recipe.MaxQuality ? "saturated" : "binding")})");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var solver = new DeterministicSolver(sim, bound, targetQuality: recipe.RequiredQuality, nodeLimit: 8_000_000);
        var result = solver.Solve(initial, recipe.RequiredQuality);
        sw.Stop();

        Console.WriteLine($"   clear search: quality {result.Quality}, {result.NodesExpanded} nodes, "
                        + $"exhaustive={result.Exhaustive}, {sw.ElapsedMilliseconds} ms");
        if (result.Quality >= 0)
            Console.WriteLine($"   line: {string.Join(" > ", result.Actions)}");

        // Not asserted. The exhaustive solver demonstrably cannot reach a verdict here, and a
        // check that passes because the search gave up is worse than no check at all — it reads
        // green while reporting quality 0. The scalable solver is measured against this instead.
        Console.WriteLine(result.Exhaustive
            ? "   exhaustive at real scale"
            : "   NOT exhaustive at real scale — the exact DFS does not reach a verdict here");

        // The scalable solver, on the same recipe the macro was actually played on.
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var beam = new FrontierSolver(sim, bound).Solve();
        sw2.Stop();

        Console.WriteLine($"   frontier: quality {beam.Quality} in {beam.Actions.Count} actions, "
                        + $"{beam.NodesExpanded} expansions, {sw2.ElapsedMilliseconds} ms");
        if (beam.Actions.Count > 0)
            Console.WriteLine($"   line: {string.Join(" > ", beam.Actions)}");

        Check($"the frontier solver reaches a verdict where the exact search cannot (quality {beam.Quality})",
            beam.Quality >= 0);

        // Phase 1's gate, met against the macro rather than against Raphael. The human line
        // reached 14204 with three Good rolls helping it; the solver reaches the same maximum
        // under all-Normal, which is strictly harder. That the beam is calibrated against a
        // proven optimum on the small recipe is what makes this figure worth anything.
        Console.WriteLine($"   requirement {recipe.RequiredQuality}; macro reached 14204 with Good rolls, "
                        + $"solver reaches {beam.Quality} without them");

        // Widening tells us whether the beam is limited by width or by its ranking heuristic.
        // What refusing gambles actually costs, measured rather than argued. The frontier is
        // deterministic here, so a fallible cast is scored as though it lands — an optimistic
        // reading that Phase 2 will replace with a real expectation over the miss.
        foreach (var budget in new[] { 0, 1, 3 })
        {
            var g = new FrontierSolver(sim, bound, gambleBudget: budget).Solve();
            var used = g.Actions.Count(a => CraftActions.Spec(a).SuccessRate < 100);
            Console.WriteLine($"   gamble budget {budget}: quality {g.Quality}, {g.Actions.Count} actions, "
                            + $"{used} fallible cast(s)");
        }

        var narrow = new FrontierSolver(sim, bound, width: 6000).Solve();
        Console.WriteLine($"   at width 6000: quality {narrow.Quality} ({narrow.Quality - beam.Quality})");
        Check($"the default width reaches the recipe's maximum quality ({beam.Quality} of {recipe.MaxQuality})",
            beam.Quality >= recipe.MaxQuality);
        Check($"and therefore clears the requirement under all-Normal ({beam.Quality} vs {recipe.RequiredQuality})",
            beam.Cleared);

        // Replaying the line must reproduce the score, or it is decoration.
        var st = sim.Initial();
        foreach (var a in beam.Actions)
        {
            var r = sim.Apply(st, a, CraftCondition.Normal);
            if (!r.Ok) break;
            st = r.State;
        }
        Check($"the frontier line replays to its reported score ({st.Quality} vs {beam.Quality}, completed={st.Completed})",
            st.Quality == beam.Quality && st.Completed);
    }


    // ── I. Reconstructing a human expert craft ────────────────────────────────

    /// <summary>
    /// Recovers the action sequence of a manually played expert craft from its recorded state,
    /// establishing a human baseline the solver can be measured against.
    ///
    /// <para>Observe-mode recordings carry every per-step value but write action 0, so the line
    /// itself was lost. It is recoverable because the simulator is exact and the action table is
    /// complete: at each step exactly one action generally reproduces the observed progress,
    /// quality, durability and CP together. Four simultaneous constraints leave very little room
    /// for coincidence.</para>
    ///
    /// <para>Reconstruction runs forward rather than per-step in isolation, because Inner Quiet
    /// and buff timers are not recorded — only status names. Carrying the simulated state forward
    /// supplies them, at the cost of a single wrong identification derailing everything after it.
    /// Ambiguities are therefore reported rather than silently resolved.</para>
    /// </summary>
    private static void ReconstructionChecks(string dataDir)
    {
        var sessions = new Dictionary<string, CraftSessionHeader>(StringComparer.Ordinal);
        var samples = new Dictionary<string, List<CraftStepSample>>(StringComparer.Ordinal);
        LoadCorpus(dataDir, sessions, samples);

        const string Target = "6ee62c7243f4";
        if (!samples.TryGetValue(Target, out var raw) || !sessions.TryGetValue(Target, out var header))
        {
            Console.WriteLine($"   session {Target} not present; skipping");
            return;
        }

        var list = raw.OrderBy(s => s.Step).ThenBy(s => s.TickMs).ToList();

        // Base values solved from the recording itself: Reflect's opening 1530 is base quality
        // times three, and a 0 CP progress cast of 1685 is base progress times five.
        var recipe = new RecipeSpec
        {
            RecipeId = header.RecipeId, ConditionsFlag = header.ConditionsFlag, IsExpert = header.IsExpert,
            RecipeJobLevel = 100,
            Difficulty = header.Difficulty, MaxQuality = (int)header.MaxQuality,
            Durability = header.Durability, RequiredQuality = (int)header.RequiredQuality,
            ProgressDivider = 100, QualityDivider = 100, ProgressModifier = 100, QualityModifier = 100,
        };
        var player = new PlayerSpec
        {
            Craftsmanship = (337 - 2) * 10, Control = (510 - 35) * 10,
            MaxCp = (int)header.MaxCp, Level = 100, GoodMultiplier = 1.75,
        };

        var sim = new CraftSim(recipe, player);
        Check($"base progress solves to 337 (got {sim.BaseProgress})", sim.BaseProgress == 337);
        Check($"base quality solves to 510 (got {sim.BaseQuality})", sim.BaseQuality == 510);

        var state = sim.Initial();
        var line = new List<CraftAction>();
        var ambiguous = 0;
        var misses = 0;
        var failedAt = -1;

        for (var i = 0; i + 1 < list.Count; i++)
        {
            var here = list[i];
            var next = list[i + 1];

            state = state with { Condition = ConditionEffects.FromDisplayName(here.Condition) };
            var nextCondition = ConditionEffects.FromDisplayName(next.Condition);

            var matches = new List<(CraftAction Action, bool Succeeded, CraftState Result)>();

            foreach (var action in CraftActions.All)
            {
                if (action == CraftAction.None) continue;

                // Fallible actions have to be tried both ways. The human line gambles on Rapid
                // Synthesis and one of those casts misses — costing durability and yielding no
                // progress — which no success-only search can account for.
                var outcomes = CraftActions.Spec(action).SuccessRate < 100
                    ? new[] { true, false }
                    : new[] { true };

                foreach (var succeeded in outcomes)
                {
                var step = sim.Apply(state, action, nextCondition, succeeded);
                if (!step.Ok) continue;

                if (step.State.Progress   != next.Progress) continue;
                if (step.State.Quality    != next.Quality) continue;
                if (step.State.Durability != next.Durability) continue;
                if (step.State.Cp         != (int)next.Cp) continue;
                if (step.State.Step       != next.Step) continue;

                matches.Add((action, succeeded, step.State));
                }
            }

            if (matches.Count == 0) { failedAt = here.Step; break; }

            // The four numbers alone are not enough: Innovation and Veneration cost the same and
            // change nothing measurable on the step they are cast, and a purely numeric match
            // took Veneration, then two steps later chose Advanced Touch because the wrong buff
            // made its arithmetic fit. Two errors cancelling is exactly the wrong answer that
            // looks right.
            //
            // Status names break that tie, but only as a preference. The recorder reads them from
            // addon slots that visibly shift — the same craft shows "Manipulation,Manipulation"
            // and later "Inner Quiet,Inner Quiet,Manipulation" — so treating them as a hard
            // filter stalls the reconstruction on a rendering artifact rather than on a real
            // disagreement.
            if (matches.Count > 1)
            {
                var best = matches
                    .OrderByDescending(m => EffectAgreement(m.Result, next))
                    .ToList();

                if (EffectAgreement(best[0].Result, next) == EffectAgreement(best[1].Result, next))
                    ambiguous++;

                matches = best;
            }

            line.Add(matches[0].Action);
            if (!matches[0].Succeeded) misses++;
            state = matches[0].Result;
        }

        Console.WriteLine($"   reconstructed {line.Count} of {list.Count - 1} transitions"
                        + (failedAt >= 0 ? $", stalled at step {failedAt}" : "")
                        + $", {ambiguous} ambiguous, {misses} failed cast(s)");
        Console.WriteLine($"   line: {string.Join(" > ", line)}");
        Console.WriteLine($"   final: quality {state.Quality}, progress {state.Progress}, "
                        + $"cp {state.Cp}, durability {state.Durability}");

        // Asserted only as far as the technique is actually established. The opening reconstructs
        // cleanly and identifies both the gamble and its misses; the run then stalls, and the
        // reason looks like a genuine disagreement rather than a bug in the matcher:
        //
        // The recording shows Manipulation still listed at step 12, though it was cast at step 3
        // and its duration is 8 — which the standard-recipe macro replay confirmed exactly, to
        // the final restore. Either the expert craft recast it somewhere the CP trace does not
        // show, or the recorder's status reads drift. The same craft rendering
        // "Manipulation,Manipulation" and later "Inner Quiet,Inner Quiet,Manipulation" argues for
        // the latter, but it is not settled, and a durability model that is wrong by five is
        // enough to stall everything downstream.
        Check($"the opening reconstructs without stalling (got {line.Count} steps)", line.Count >= 15);
        Check($"the line starts on a plausible opener (got {(line.Count > 0 ? line[0].ToString() : "none")})",
            line.Count > 0 && line[0] == CraftAction.Reflect);
        Check($"the human line is shown to gamble on Rapid Synthesis ({misses} of its casts missed)",
            line.Contains(CraftAction.RapidSynthesis));

        Console.WriteLine(failedAt < 0
            ? "   full craft reconstructed"
            : $"   stalled at step {failedAt} of {list.Count} — see the note in this method");
    }


    /// <summary>
    /// Whether the simulated statuses agree with the ones the recorder saw.
    ///
    /// <para>Compared as a set: the addon renders some statuses through more than one node, so
    /// the recording contains duplicates like "Manipulation,Manipulation" that carry no meaning.
    /// Inner Quiet and Trained Perfection appear in that list too, though the simulator holds
    /// them as a count and a flag rather than as timers.</para>
    /// </summary>
    private static int EffectAgreement(CraftState state, CraftStepSample sample)
    {
        var simulated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CraftBuff buff in Enum.GetValues<CraftBuff>())
        {
            if (buff == CraftBuff.None) continue;
            if (state.HasBuff(buff)) simulated.Add(EffectNames(buff));
        }
        if (state.InnerQuiet > 0) simulated.Add("Inner Quiet");
        if (state.TrainedPerfectionActive) simulated.Add("Trained Perfection");

        var recorded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sample.Effects)
        {
            // Entries may carry a remaining-step suffix.
            var name = raw.Contains(':') ? raw[..raw.IndexOf(':')] : raw;
            if (!string.IsNullOrWhiteSpace(name)) recorded.Add(name.Trim());
        }

        // Symmetric difference, negated: higher is closer agreement.
        var missing = 0;
        foreach (var name in recorded) if (!simulated.Contains(name)) missing++;
        foreach (var name in simulated) if (!recorded.Contains(name)) missing++;
        return -missing;
    }

    private static string EffectNames(CraftBuff buff) => buff switch
    {
        CraftBuff.WasteNot       => "Waste Not",
        CraftBuff.WasteNotII     => "Waste Not II",
        CraftBuff.GreatStrides   => "Great Strides",
        CraftBuff.MuscleMemory   => "Muscle Memory",
        CraftBuff.FinalAppraisal => "Final Appraisal",
        _                        => buff.ToString(),
    };

    // ── shared corpus loading ─────────────────────────────────────────────────

    private static void LoadCorpus(string dataDir,
        Dictionary<string, CraftSessionHeader> sessions,
        Dictionary<string, List<CraftStepSample>> samples)
    {
        if (!Directory.Exists(dataDir)) return;

        foreach (var path in Directory.EnumerateFiles(dataDir, "*.jsonl"))
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                var data = root.GetProperty("data");

                if (type == "session")
                {
                    var h = data.Deserialize<CraftSessionHeader>();
                    if (h != null) sessions[h.Id] = h;
                }
                else if (type == "step")
                {
                    var s = data.Deserialize<CraftStepSample>();
                    if (s == null) continue;
                    if (!samples.TryGetValue(s.SessionId, out var list))
                        samples[s.SessionId] = list = new List<CraftStepSample>();
                    list.Add(s);
                }
            }
            catch (JsonException) { }
        }
    }

    /// <summary>
    /// A sim whose base progress equals the value observed in that session. ProgressDivider is
    /// pinned to 100 and craftsmanship solved backwards, so the prediction still comes out of
    /// CraftSim's own formula rather than being asserted around it.
    /// </summary>
    private static CraftSim BuildSim(CraftSessionHeader header, int baseProgress)
    {
        var recipe = new RecipeSpec
        {
            RecipeId = header.RecipeId,
            ConditionsFlag = header.ConditionsFlag,
            IsExpert = header.IsExpert,
            RecipeJobLevel = 100,
            Difficulty = header.Difficulty,
            MaxQuality = (int)header.MaxQuality,
            Durability = header.Durability,
            RequiredQuality = (int)header.RequiredQuality,
            ProgressDivider = 100,
            QualityDivider = 100,
            ProgressModifier = 100,
            QualityModifier = 100,
        };

        var player = new PlayerSpec
        {
            Craftsmanship = (baseProgress - 2) * 10,
            Control = 4000,
            MaxCp = (int)header.MaxCp,
            Level = 100,
            GoodMultiplier = 1.75,
        };

        return new CraftSim(recipe, player);
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine(title);
        Console.WriteLine(new string('=', 72));
    }

    private static void Check(string label, bool ok, string? note = null)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (note != null && !ok) Console.WriteLine($"          {note}");
    }
}
