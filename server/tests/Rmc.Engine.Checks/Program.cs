using System.Globalization;
using System.Text.RegularExpressions;
using Rmc.Engine;
using Rmc.Engine.Damage;
using Rmc.Engine.Sim;
using Rmc.Engine.Sim.Rotations;
using Rmc.GameData;

// Phase 2 gate: the stat engine reproduces xivgear's computed stats for every BiS set, agrees
// with the GCD each Balance set is labelled with, and matches any in-game attribute panel
// observations we have. Run from server/.

var snapshotPath = args.Length > 0 ? args[0] : Directory.EnumerateFiles(Path.Combine("data", "gamedata"), "*.json").Order(StringComparer.Ordinal).Last();
var data = new GameDataIndex(GameDataJson.ReadSnapshot(snapshotPath));
var engine = new StatEngine(data);
var bis = Directory.EnumerateFiles(Path.Combine("data", "bis"), "*.json", SearchOption.AllDirectories)
    .Order(StringComparer.Ordinal)
    .Select(f => (File: f, Sheet: GameDataJson.Read<BisSheet>(f)))
    .ToList();
Console.WriteLine($"Snapshot {snapshotPath}: patch {data.Snapshot.Meta.Patch}; {bis.Sum(b => b.Sheet.Sets.Count)} BiS sets");

var checks = new Checks();
checks.Run("formula spot values", FormulaSpotValues);
checks.Run("breakpoints bracket every stat", () => BreakpointSanity(engine, bis));
checks.Run("BiS sets compute without issues", () => NoIssues(engine, bis));
checks.Run("xivgear parity (computed stats, every BiS set)", () => XivgearParity(engine, bis, Path.Combine("data", "reference", "xivgear", "computed")));
checks.Run("Balance GCD labels", () => GcdLabels(engine, bis));
checks.Run("in-game panel observations", () => PanelObservations(engine, Path.Combine("data", "reference", "ingame")));
checks.Run("sim mechanics", () => SimMechanics(engine, bis));
checks.Run("rotation sims vs xivgear (relative 0.3%, speed variants 2%, absolute 2%)", () => SimCalibration(engine, bis));
checks.Run("fitted profiles reproduce the sims (0.1%)", () => FittedProfiles(engine, bis));
checks.Run("meld solver matches or beats every BiS sheet's melds", () => MeldSolverVsBis(engine, bis));
checks.Run("meld solver from random melds (fixed seeds)", () => MeldSolverRandomStarts(engine, bis));
checks.Run("meld solver options (piety floor, food)", () => MeldSolverOptions(engine, bis));
checks.Run("upgrade planner (weekly budget, chains, completion, toggles)", () => UpgradePlans(engine, bis));
return checks.Finish();

// Values that can be worked out by hand from the formulas at level 100 (sub 420, main 440, div 2780).
static IEnumerable<string> FormulaSpotValues()
{
    var l = new LevelInfo { Level = 100, LevelDiv = 2780, BaseMainStat = 440, BaseSubStat = 420, Hp = 4000, HpScalarTank = 430, HpScalarOther = 301, MainStatPowerModTank = 190, MainStatPowerModOther = 237 };
    var cases = new (string What, int Actual, int Expected)[]
    {
        ("GCD at base speed", StatMath.GcdCentiseconds(l, 420), 250),
        ("GCD one tier above base", StatMath.GcdCentiseconds(l, 442), 249),
        ("GCD just below that tier", StatMath.GcdCentiseconds(l, 441), 250),
        ("GCD with 20% haste at base", StatMath.GcdCentiseconds(l, 420, hastePercent: 20), 200),
        ("crit rate at base", StatMath.CritRatePerMille(l, 420), 50),
        ("crit damage at base", StatMath.CritDamagePerMille(l, 420), 1400),
        ("crit rate at 3605", StatMath.CritRatePerMille(l, 3605), 279),
        ("DH rate at 1982", StatMath.DirectHitRatePerMille(l, 1982), 309),
        ("det at 2506", StatMath.DeterminationPerMille(l, 2506), 1104),
        ("main stat % (melee, 6838)", StatMath.MainStatPercent(l, "Melee", 6838), 3546),
        ("WD % (DRG mod 115, WD 158)", StatMath.WeaponDamagePercent(l, 115, 158), 208),
        ("tier start of crit tier 1", StatMath.TierStart(200, 1, 420, 2780), 434),
        ("tier start below base", StatMath.TierStart(200, -1, 420, 2780), 407),
    };
    foreach (var (what, actual, expected) in cases.Where(c => c.Actual != c.Expected))
        yield return $"{what}: {actual}, expected {expected}";
}

static IEnumerable<string> BreakpointSanity(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    foreach (var (_, sheet) in bis)
    foreach (var set in sheet.Sets)
    {
        var stats = engine.Compute(GearsetInputs.FromBis(sheet.Meta, set));
        foreach (var b in stats.Breakpoints)
        {
            if (!(b.TierStart <= b.Value && b.Value < b.NextTier))
                yield return $"{sheet.Meta.Job} {set.Name}: {b.Stat} {b.Value} not in [{b.TierStart}, {b.NextTier})";
            if (b.GcdCs is int gcd && b.NextGcdCs is int next && next >= gcd)
                yield return $"{sheet.Meta.Job} {set.Name}: {b.Stat} next GCD tier {next} is not faster than {gcd}";
        }
    }
}

static IEnumerable<string> NoIssues(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    foreach (var (_, sheet) in bis)
    foreach (var set in sheet.Sets)
    foreach (var issue in engine.Compute(GearsetInputs.FromBis(sheet.Meta, set)).Issues.Where(i => i.IsError))
        yield return $"{sheet.Meta.Job} {set.Name}: {issue.Message}";
}

static IEnumerable<string> XivgearParity(StatEngine engine, List<(string File, BisSheet Sheet)> bis, string dir)
{
    if (!Directory.Exists(dir))
    {
        yield return $"no reference at {dir} (run rmc-extract snapshot-xivgear)";
        yield break;
    }
    var compared = 0;
    foreach (var (file, sheet) in bis)
    {
        var refPath = Path.Combine(dir, $"{sheet.Meta.Job}_{Path.GetFileNameWithoutExtension(file)}.json");
        if (!File.Exists(refPath))
        {
            yield return $"{sheet.Meta.Job}: no computed reference";
            continue;
        }
        var reference = GameDataJson.Read<List<ComputedRef>>(refPath);
        for (var i = 0; i < sheet.Sets.Count && i < reference.Count; i++)
        {
            var ours = Flatten(engine.Compute(GearsetInputs.FromBis(sheet.Meta, sheet.Sets[i])));
            foreach (var (field, expected) in reference[i].Stats)
            {
                if (!ours.TryGetValue(field, out var actual))
                    continue;
                compared++;
                if (Math.Abs(actual - expected) > 1e-9)
                    yield return $"{sheet.Meta.Job} '{sheet.Sets[i].Name}': {field} {actual.ToString(CultureInfo.InvariantCulture)} vs xivgear {expected.ToString(CultureInfo.InvariantCulture)}";
            }
        }
    }
    if (compared == 0)
        yield return "nothing compared";
    else
        Console.WriteLine($"          ({compared} values compared)");
}

// Our results under xivgear's computedStats field names and units.
static Dictionary<string, double> Flatten(GearsetStats s)
{
    double F(string stat) => s.Final.GetValueOrDefault(stat);
    return new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["strength"] = F(Stats.Strength), ["dexterity"] = F(Stats.Dexterity), ["vitality"] = F(Stats.Vitality),
        ["intelligence"] = F(Stats.Intelligence), ["mind"] = F(Stats.Mind), ["piety"] = F(Stats.Piety),
        ["crit"] = F(Stats.Critical), ["determination"] = F(Stats.Determination), ["dhit"] = F(Stats.DirectHit),
        ["skillspeed"] = F(Stats.SkillSpeed), ["spellspeed"] = F(Stats.SpellSpeed), ["tenacity"] = F(Stats.Tenacity),
        ["wdPhys"] = s.WeaponPhysical, ["wdMag"] = s.WeaponMagical, ["weaponDelay"] = s.WeaponDelayMs / 1000.0,
        ["hp"] = s.Hp, ["mpPerTick"] = s.MpPerTick,
        ["critChance"] = s.CritRatePerMille / 1000.0, ["critMulti"] = s.CritDamagePerMille / 1000.0,
        ["dhitChance"] = s.DirectHitRatePerMille / 1000.0, ["detMulti"] = s.DeterminationPerMille / 1000.0,
        ["mainStatMulti"] = s.MainStatPercent / 100.0, ["aaStatMulti"] = s.AutoAttackStatPercent / 100.0,
        ["wdMulti"] = s.WeaponDamagePercent / 100.0, ["aaMulti"] = s.AutoAttackPercent / 100.0,
        ["tncMulti"] = s.TenacityPerMille / 1000.0, ["tncIncomingMulti"] = s.TenacityIncomingPerMille / 1000.0,
        ["sksDotMulti"] = s.SkillSpeedDotPerMille / 1000.0, ["spsDotMulti"] = s.SpellSpeedDotPerMille / 1000.0,
        ["autoDhBonus"] = s.AutoDirectHitBonusPerMille / 1000.0,
    };
}

// Balance set names lead with their GCD. Jobs with permanent haste label the hasted GCD.
static IEnumerable<string> GcdLabels(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    var haste = new Dictionary<string, int> { ["MNK"] = 20, ["NIN"] = 15, ["SAM"] = 13, ["VPR"] = 15 };
    // Labels that group a set with neighbouring builds rather than state its exact GCD.
    var looseLabels = new Dictionary<string, string>
    {
        // 796 SkS is in the 2.13 tier (2.14 ends at 795); the sheet files it with its 2.14 builds.
        ["SAM|2.14 Pentamelded Crafted"] = "2.13",
    };
    var label = new Regex(@"(?<![\d.])([12]\.\d{1,2})(?![\d])", RegexOptions.CultureInvariant);
    var labelled = 0;
    foreach (var (_, sheet) in bis)
    {
        var level = engine.Data.Snapshot.Levels.First(l => l.Level == sheet.Meta.Level);
        foreach (var set in sheet.Sets)
        {
            var m = label.Matches(set.Name).Select(x => decimal.Parse(x.Value, CultureInfo.InvariantCulture)).FirstOrDefault(v => v is >= 1.5m and <= 2.5m);
            if (m == 0)
                continue;
            labelled++;
            var stats = engine.Compute(GearsetInputs.FromBis(sheet.Meta, set));
            var speedStat = stats.Role is "Healer" or "Caster" ? Stats.SpellSpeed : Stats.SkillSpeed;
            var gcd = StatMath.GcdCentiseconds(level, stats.Final[speedStat], hastePercent: haste.GetValueOrDefault(sheet.Meta.Job));
            if (looseLabels.TryGetValue($"{sheet.Meta.Job}|{set.Name}", out var actualLabel))
                m = decimal.Parse(actualLabel, CultureInfo.InvariantCulture);
            if (gcd != (int)(m * 100))
                yield return string.Create(CultureInfo.InvariantCulture, $"{sheet.Meta.Job} '{set.Name}': computed GCD {gcd / 100.0:0.00} ({speedStat} {stats.Final[speedStat]})");
        }
    }
    Console.WriteLine($"          ({labelled} labelled sets)");
}

// In-game attribute panel readings (reference/ingame/*.json): gear as the plugin reads it plus
// the panel's numbers. Filled in once the plugin sends them (phase 6); none yet is not a failure.
static IEnumerable<string> PanelObservations(StatEngine engine, string dir)
{
    if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "*.json").Any())
    {
        Console.WriteLine("          (no observations recorded yet)");
        yield break;
    }
    foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
    {
        var obs = GameDataJson.Read<PanelObservation>(file);
        var stats = engine.Compute(obs.Gear);
        foreach (var (stat, expected) in obs.Panel)
        {
            var actual = stats.Final.GetValueOrDefault(stat);
            if (actual != expected)
                yield return $"{Path.GetFileName(file)}: {stat} {actual}, panel shows {expected}";
        }
    }
}

// Phase 4 gate: re-meld every BiS set from empty slots. At the set's own GCD and unconstrained,
// the solver must reach at least the sheet's DPS under our model (its melds are one of the
// candidates), its fast stat path must agree with a full recompute, and grade rules must hold.
static IEnumerable<string> MeldSolverVsBis(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    var models = new Dictionary<string, FittedProfileModel>(StringComparer.Ordinal);
    var totalMs = 0.0;
    var solves = 0;
    var gains = new List<double>();
    double fastLoss = 0, fastMs = 0;
    foreach (var (_, sheet) in bis)
    {
        var job = sheet.Meta.Job;
        if (!models.TryGetValue(job, out var model))
        {
            var path = Path.Combine("data", "profiles", $"{job}.json");
            if (!File.Exists(path))
                continue;
            models[job] = model = new FittedProfileModel(GameDataJson.Read<HitProfile>(path));
        }
        var solver = new Rmc.Engine.Melds.MeldSolver(engine, model);
        foreach (var set in sheet.Sets)
        {
            var input = GearsetInputs.FromBis(sheet.Meta, set);
            var own = model.Evaluate(engine.Compute(input), null).GcdCs;
            foreach (var target in new int?[] { own, null })
            {
                var solution = solver.Solve(input, new Rmc.Engine.Melds.MeldSolveOptions { TargetGcdCs = target });
                totalMs += solution.Elapsed.TotalMilliseconds;
                solves++;
                var label = target == null ? "unconstrained" : string.Create(CultureInfo.InvariantCulture, $"at {target / 100.0:0.00}");
                if (solution.Infeasible)
                {
                    yield return $"{job} '{set.Name.Trim()}' {label}: no solution";
                    continue;
                }
                if (solution.Dps < solution.CurrentDps - 1e-6)
                    yield return string.Create(CultureInfo.InvariantCulture, $"{job} '{set.Name.Trim()}' {label}: solved {solution.Dps:0.00} < sheet {solution.CurrentDps:0.00}");
                if (Math.Abs(solution.SearchDps - solution.SearchDpsRecomputed) > 1e-6)
                    yield return string.Create(CultureInfo.InvariantCulture, $"{job} '{set.Name.Trim()}' {label}: search scored {solution.SearchDps:0.00} but recomputes to {solution.SearchDpsRecomputed:0.00}");
                if (target == null)
                {
                    gains.Add(solution.Dps / solution.CurrentDps - 1);
                    var fast = solver.Solve(input, new Rmc.Engine.Melds.MeldSolveOptions { Fast = true });
                    fastLoss = Math.Max(fastLoss, 1 - fast.Dps / solution.Dps);
                    fastMs += fast.Elapsed.TotalMilliseconds;
                    if (fast.Dps < solution.Dps * 0.999)
                        yield return string.Create(CultureInfo.InvariantCulture, $"{job} '{set.Name.Trim()}': fast mode {fast.Dps:0.00} more than 0.1% below exact {solution.Dps:0.00}");
                }
                foreach (var (slot, piece) in solution.Gear.Slots)
                {
                    var item = engine.Data.Item(piece.ItemId)!;
                    var allowed = item.AdvancedMelding ? Rmc.GameData.GearRules.MaxMateriaSlots : item.MateriaSlots;
                    if (piece.Materia.Count > allowed)
                        yield return $"{job} '{set.Name.Trim()}' {slot}: {piece.Materia.Count} melds in {allowed} slots";
                    for (var i = 0; i < piece.Materia.Count; i++)
                        if (!Rmc.GameData.GearRules.GradeAllowedInSlot(piece.Materia[i].Grade, i, item.MateriaSlots))
                            yield return $"{job} '{set.Name.Trim()}' {slot}: grade {piece.Materia[i].Grade} in slot {i + 1}";
                }
            }
        }
    }
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"          {solves} exact solves, {totalMs / solves:0} ms average; unconstrained gain over the sheets: median {gains.Order().ElementAt(gains.Count / 2):+0.00%}, max {gains.Max():+0.00%}"));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"          fast mode: {fastMs / gains.Count:0} ms average, worst {fastLoss:0.000%} below exact"));
}

// Random, unoptimal melds on each job's BiS sets (fixed seeds, so failures reproduce):
// - every slot filled at random: the solver must improve on it, reach at least the sheet's melds,
//   and land on exactly the set it finds from empty slots (it re-melds everything);
// - keep-existing mode with a random prefix of melds kept per piece: the kept melds stay, and the
//   result beats the partial set;
// - one random slot locked with random melds: it comes back unchanged.
static IEnumerable<string> MeldSolverRandomStarts(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    const int startsPerSet = 3;
    var losses = new List<double>();
    var solves = 0;
    foreach (var (_, sheet) in bis)
    {
        var job = sheet.Meta.Job;
        var path = Path.Combine("data", "profiles", $"{job}.json");
        if (!File.Exists(path))
            continue;
        var model = new FittedProfileModel(GameDataJson.Read<HitProfile>(path));
        var solver = new Rmc.Engine.Melds.MeldSolver(engine, model);
        var role = engine.Data.Job(job)!.Role;
        var stats = new List<string> { Stats.Critical, Stats.DirectHit, Stats.Determination, model.Profile.SpeedStat == "sps" ? Stats.SpellSpeed : Stats.SkillSpeed };
        if (role == "Tank") stats.Add(Stats.Tenacity);
        var medicine = RotationRegistry.For(job)!.UsesPotion ? engine.BestMedicine(job) : null;
        double Dps(GearsetInput g) { var st = engine.Compute(g); return model.Evaluate(st, medicine != null ? engine.WithMedicine(st, medicine) : null).Dps; }

        foreach (var set in sheet.Sets.Take(1))
        {
            var bisInput = GearsetInputs.FromBis(sheet.Meta, set);
            var sheetDps = Dps(bisInput);
            var fromEmpty = solver.Solve(bisInput);
            var name = $"{job} '{set.Name.Trim()}'";
            for (var k = 0; k < startsPerSet; k++)
            {
                var rng = new Random(HashCode.Combine(StringComparer.Ordinal.GetHashCode(set.Name), k) & 0x7fffffff ^ k * 7919);
                var random = RandomMelds(engine, bisInput, stats, rng);
                var randomDps = Dps(random);
                var solved = solver.Solve(random);
                solves++;
                losses.Add(randomDps / solved.Dps - 1);
                if (solved.Dps < randomDps - 1e-6)
                    yield return string.Create(CultureInfo.InvariantCulture, $"{name} seed {k}: solved {solved.Dps:0.00} below the random melds {randomDps:0.00}");
                if (solved.Dps < sheetDps - 1e-6)
                    yield return string.Create(CultureInfo.InvariantCulture, $"{name} seed {k}: solved {solved.Dps:0.00} below the sheet {sheetDps:0.00}");
                if (Math.Abs(solved.Dps - fromEmpty.Dps) > 1e-6)
                    yield return string.Create(CultureInfo.InvariantCulture, $"{name} seed {k}: from random melds {solved.Dps:0.00}, from empty slots {fromEmpty.Dps:0.00}");

                // Keep a random prefix of each piece's melds; fill the rest.
                var partial = Clone(random);
                foreach (var piece in partial.Slots.Values)
                    piece.Materia.RemoveRange(rng.Next(piece.Materia.Count + 1) is var keep ? keep : 0, piece.Materia.Count - keep);
                var kept = solver.Solve(partial, new Rmc.Engine.Melds.MeldSolveOptions { KeepExistingMelds = true });
                solves++;
                if (kept.Dps < Dps(partial) - 1e-6)
                    yield return string.Create(CultureInfo.InvariantCulture, $"{name} seed {k}: keep-existing {kept.Dps:0.00} below the partial set {Dps(partial):0.00}");
                foreach (var (slot, piece) in partial.Slots)
                {
                    var result = kept.Gear.Slots[slot].Materia;
                    if (result.Count < piece.Materia.Count || piece.Materia.Where((m, i) => result[i].Stat != m.Stat || result[i].Grade != m.Grade).Any())
                        yield return $"{name} seed {k}: keep-existing changed the kept melds on {slot}";
                }

                // Lock one slot with its random melds.
                var lockedSlot = random.Slots.Keys.Order(StringComparer.Ordinal).ElementAt(rng.Next(random.Slots.Count));
                var locked = solver.Solve(random, new Rmc.Engine.Melds.MeldSolveOptions { LockedSlots = [lockedSlot] });
                solves++;
                var before = random.Slots[lockedSlot].Materia.Select(m => $"{m.Stat}{m.Grade}");
                var after = locked.Gear.Slots[lockedSlot].Materia.Select(m => $"{m.Stat}{m.Grade}");
                if (!before.SequenceEqual(after))
                    yield return $"{name} seed {k}: locked {lockedSlot} changed ({string.Join(" ", before)} -> {string.Join(" ", after)})";
                if (locked.Dps < randomDps - 1e-6)
                    yield return string.Create(CultureInfo.InvariantCulture, $"{name} seed {k}: with {lockedSlot} locked {locked.Dps:0.00} below the random melds {randomDps:0.00}");
            }
        }
    }
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"          {solves} solves; random melds were {-losses.Average():0.00%} below the solver on average (worst {-losses.Min():0.00%})"));
}

static GearsetInput RandomMelds(StatEngine engine, GearsetInput input, List<string> stats, Random rng)
{
    var copy = Clone(input);
    foreach (var piece in copy.Slots.Values)
    {
        piece.Materia.Clear();
        var item = engine.Data.Item(piece.ItemId);
        if (item == null) continue;
        var total = item.AdvancedMelding ? Rmc.GameData.GearRules.MaxMateriaSlots : item.MateriaSlots;
        for (var i = 0; i < total; i++)
            piece.Materia.Add(new MeldInput
            {
                Stat = stats[rng.Next(stats.Count)],
                Grade = Rmc.GameData.GearRules.GradeAllowedInSlot(12, i, item.MateriaSlots) ? 12 : 11,
            });
    }
    return copy;
}

static GearsetInput Clone(GearsetInput input) => new()
{
    Job = input.Job, Level = input.Level, ClanId = input.ClanId, PartyBonus = input.PartyBonus, Food = input.Food,
    Slots = input.Slots.ToDictionary(kv => kv.Key, kv => new PieceInput
    {
        ItemId = kv.Value.ItemId, Hq = kv.Value.Hq, RelicStats = kv.Value.RelicStats,
        Materia = kv.Value.Materia.Select(m => new MeldInput { Stat = m.Stat, Grade = m.Grade }).ToList(),
    }, StringComparer.Ordinal),
};

// Options on each job's first BiS set: a piety floor (healers) is met and costs DPS rather than
// gaining it; solving the food never does worse than keeping the sheet's.
static IEnumerable<string> MeldSolverOptions(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    foreach (var (_, sheet) in bis.GroupBy(b => b.Sheet.Meta.Job).Select(g => g.First()))
    {
        var job = sheet.Meta.Job;
        var path = Path.Combine("data", "profiles", $"{job}.json");
        if (!File.Exists(path))
            continue;
        var solver = new Rmc.Engine.Melds.MeldSolver(engine, new FittedProfileModel(GameDataJson.Read<HitProfile>(path)));
        var input = GearsetInputs.FromBis(sheet.Meta, sheet.Sets[0]);
        var free = solver.Solve(input);
        var withFood = solver.Solve(input, new Rmc.Engine.Melds.MeldSolveOptions { SolveFood = true });
        if (withFood.Dps < free.Dps - 1e-6)
            yield return string.Create(CultureInfo.InvariantCulture, $"{job}: solving food {withFood.Dps:0.00} below the sheet's food {free.Dps:0.00}");
        if (engine.Data.Job(job)!.Role != "Healer")
            continue;
        var floor = free.Stats.Final.GetValueOrDefault(Stats.Piety) + 150;
        var pious = solver.Solve(input, new Rmc.Engine.Melds.MeldSolveOptions { MinPiety = floor });
        if (pious.Infeasible)
            yield return $"{job}: no set reaches {floor} piety";
        else if (pious.Stats.Final.GetValueOrDefault(Stats.Piety) < floor)
            yield return $"{job}: piety {pious.Stats.Final.GetValueOrDefault(Stats.Piety)} below the floor {floor}";
        else if (pious.Dps > free.Dps + 1e-6)
            yield return string.Create(CultureInfo.InvariantCulture, $"{job}: the piety floor raised DPS ({pious.Dps:0.00} > {free.Dps:0.00})");
        else
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"          {job}: +150 piety ({floor}) costs {1 - pious.Dps / free.Dps:0.00%}; best food {withFood.Gear.Food?.ItemId} ({withFood.Dps / free.Dps - 1:+0.00%})"));
    }
}

// Phase 5 gate: plan every job from a fresh crafted set to its first BiS set, with no wallet and
// the weekly income from overlays/weekly.json, then check the plan against the rules:
// - spending never runs ahead of the income (replayed week by week);
// - an augment comes after its base piece; a step that loses DPS is a base piece whose augment
//   follows the same week;
// - every target piece lands, or is listed as drop-only or no DPS gain; nothing is left unreached
//   within the horizon, and where every target piece lands the plan ends within 0.1% of the target;
// - ignoring weekly limits puts everything in week 0 and ends at the same DPS;
// - with hunt materials, no augment spends books;
// - from the BiS set itself there is nothing to do.
static IEnumerable<string> UpgradePlans(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    var weekly = GameDataJson.Read<WeeklyIncomeFile>(Path.Combine("data", "overlays", "weekly.json"));
    var totalMs = 0.0;
    var plans = 0;
    foreach (var (_, sheet) in bis.GroupBy(b => b.Sheet.Meta.Job).Select(g => g.First()))
    {
        var job = sheet.Meta.Job;
        var path = Path.Combine("data", "profiles", $"{job}.json");
        if (!File.Exists(path))
            continue;
        var model = new FittedProfileModel(GameDataJson.Read<HitProfile>(path));
        var planner = new Rmc.Engine.Upgrades.UpgradePlanner(engine, model);
        var target = GearsetInputs.FromBis(sheet.Meta, sheet.Sets[0]);
        var start = Rmc.Engine.Upgrades.StartingSets.Crafted(engine, model, target);
        Rmc.Engine.Upgrades.UpgradeOptions Options(bool noLimits, bool hunts) => new()
        {
            WeeklyIncome = weekly.PerWeek, HuntCurrency = weekly.HuntCurrency, IgnoreWeeklyLimits = noLimits, MaterialsFromHunts = hunts,
        };

        var plan = planner.Plan(start, target, Options(false, false));
        totalMs += plan.Elapsed.TotalMilliseconds;
        plans++;
        var name = $"{job} '{sheet.Sets[0].Name.Trim()}'";

        // Budget: replay the spending week by week.
        foreach (var (currency, perWeek) in weekly.PerWeek)
        {
            var spent = 0;
            foreach (var step in plan.Steps.OrderBy(st => st.Week))
            {
                spent += step.Costs.GetValueOrDefault(currency);
                if (spent > perWeek * step.Week)
                {
                    yield return $"{name}: by week {step.Week} the plan spends {spent} {currency}, income is {perWeek * step.Week}";
                    break;
                }
            }
        }

        // Chains and losing steps.
        for (var k = 0; k < plan.Steps.Count; k++)
        {
            var step = plan.Steps[k];
            if (step.Kind == Rmc.Engine.Upgrades.StepKind.Augment)
            {
                var baseId = engine.Data.Item(step.ToItemId)!.Source.Augment!.BaseItemId;
                if (step.FromItemId != baseId)
                    yield return $"{name}: {step.Slot} augmented from item {step.FromItemId}, not its base {baseId}";
            }
            if (step.Gain < -1e-6)
            {
                var next = plan.Steps.Skip(k + 1).FirstOrDefault(st => st.Slot == step.Slot);
                if (next == null || next.Kind != Rmc.Engine.Upgrades.StepKind.Augment || next.Week != step.Week)
                    yield return string.Create(CultureInfo.InvariantCulture, $"{name}: {step.Slot} {step.ToItem} loses {-step.Gain:0.0} DPS and isn't augmented the same week");
            }
        }

        // Completion.
        foreach (var u in plan.Unreached)
            yield return $"{name}: unreached within {Options(false, false).MaxWeeks} weeks: {u}";
        var accounted = plan.Drops.Select(d => d.Slot).Concat(plan.NoGain.Select(n => n.Split(':')[0])).ToHashSet(StringComparer.Ordinal);
        foreach (var (slot, piece) in target.Slots)
            if (!accounted.Contains(slot) && plan.FinalGear.Slots.GetValueOrDefault(slot)?.ItemId != piece.ItemId)
                yield return $"{name}: {slot} ends on item {plan.FinalGear.Slots.GetValueOrDefault(slot)?.ItemId}, target {piece.ItemId}";
        if (plan.Drops.Count == 0 && plan.NoGain.Count == 0 && plan.FinalDps < plan.BisDps * 0.999)
            yield return string.Create(CultureInfo.InvariantCulture, $"{name}: plan ends at {plan.FinalDps:0.0}, target set {plan.BisDps:0.0}");

        // Toggles.
        var free = planner.Plan(start, target, Options(true, false));
        if (free.Steps.Any(st => st.Week != 0))
            yield return $"{name}: ignoring weekly limits, a step still lands after week 0";
        if (Math.Abs(free.FinalDps / plan.FinalDps - 1) > 0.001)
            yield return string.Create(CultureInfo.InvariantCulture, $"{name}: ignoring weekly limits ends at {free.FinalDps:0.0}, the timeline at {plan.FinalDps:0.0}");
        var hunts = planner.Plan(start, target, Options(false, true));
        foreach (var step in hunts.Steps.Where(st => st.Kind == Rmc.Engine.Upgrades.StepKind.Augment))
        {
            var material = engine.Data.Item(step.ToItemId)!.Source.Augment!.Materials.FirstOrDefault()?.Name;
            var huntable = engine.Data.Snapshot.Currencies.FirstOrDefault(c => c.Name == material)?.Exchanges
                .Any(e => e.All(x => x.Name == weekly.HuntCurrency)) ?? false;
            if (huntable && step.Costs.Keys.Any(weekly.PerWeek.ContainsKey))
                yield return $"{name}: with hunt materials, {step.Slot}'s augment still spends {string.Join(", ", step.Costs.Keys)}";
        }

        // Identity.
        var none = planner.Plan(target, target, Options(false, false));
        if (none.Steps.Count > 0 || none.Drops.Count > 0)
            yield return $"{name}: planning from the target itself gives {none.Steps.Count} steps and {none.Drops.Count} drops";

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"          {job}: {plan.CurrentDps:0} -> {plan.FinalDps:0} by week {plan.WeeksToFinish}, {plan.Steps.Count} steps; hunt materials: week {hunts.WeeksToFinish}; "
            + $"{plan.Drops.Count} drop-only, {plan.NoGain.Count} no-gain; {plan.Elapsed.TotalSeconds:0.0}s"));
    }
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"          {plans} plans, {totalMs / plans / 1000:0.0}s average"));
}

// The fitted hit profiles (data/profiles, from "rmc-extract fit-profiles") against the full
// sims they summarise, averaged over the same fight lengths, for every BiS set and re-meld: the
// profile only differs where the rotation reads a stat other than speed: Monk's Chakra comes from
// crits, fitted at the reference set's crit rate, so MNK gets 0.3%. Other fight lengths
// (data/profiles/<N>s, "fit-profiles --seconds N") get the same check.
static IEnumerable<string> FittedProfiles(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    var standard = Path.Combine("data", "profiles");
    foreach (var dir in Directory.EnumerateDirectories(standard).Order(StringComparer.Ordinal).Prepend(standard))
    foreach (var job in RotationRegistry.Jobs.Order(StringComparer.Ordinal))
    {
        var path = Path.Combine(dir, $"{job}.json");
        var label = dir == standard ? job : $"{job} ({Path.GetFileName(dir)})";
        if (!File.Exists(path))
        {
            yield return $"{label}: no fitted profile (run rmc-extract fit-profiles)";
            continue;
        }
        var profile = GameDataJson.Read<HitProfile>(path);
        var potencies = PotencyTable.Load(RotationRegistry.DefaultPotencyDirectory, job);
        if (potencies != null && potencies.GameVersion != profile.GameVersion)
            yield return $"{label}: profile fitted on game {profile.GameVersion}, potencies are from {potencies.GameVersion} (refit)";
        var model = new FittedProfileModel(profile);
        var sets = bis.Where(b => b.Sheet.Meta.Job == job).SelectMany(b => b.Sheet.Sets.Select(set => (b.Sheet.Meta, set))).ToList();
        var variantPath = Path.Combine("data", "reference", "xivgear", "variants", $"{job}.json");
        if (File.Exists(variantPath))
        {
            var variants = GameDataJson.Read<BisSheet>(variantPath);
            sets.AddRange(variants.Sets.Where(v => v.Name.StartsWith("RMC ", StringComparison.Ordinal)).Select(v => (variants.Meta, v)));
        }
        var worst = 0.0;
        foreach (var (meta, set) in sets)
        {
            var input = GearsetInputs.FromBis(meta, set);
            var stats = engine.Compute(input);
            var rotation = RotationRegistry.For(job)!;
            var medicine = rotation.UsesPotion ? engine.BestMedicine(job) : null;
            var fitted = model.Evaluate(stats, medicine != null ? engine.WithMedicine(stats, medicine) : null);
            var sim = profile.FightSeconds.Average(t => Simulation.Run(engine, input, RotationRegistry.For(job, RotationRegistry.DefaultPotencyDirectory)!,
                new SimSettings { FightSeconds = t }).Dps * t) / profile.FightSeconds.Average();
            var err = fitted.Dps / sim - 1;
            worst = Math.Max(worst, Math.Abs(err));
            if (Math.Abs(err) > (job == "MNK" ? 0.003 : 0.001) || fitted.OutsideFittedRange)
                yield return string.Create(CultureInfo.InvariantCulture, $"{label} '{set.Name.Trim()}': profile {fitted.Dps:0.0} vs sim {sim:0.0} ({err:+0.000%;-0.000%})")
                    + (fitted.OutsideFittedRange ? ", GCD outside the fitted tiers" : "");
        }
        if (dir != standard)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"          {label}: {sets.Count} sets, worst {worst:0.000%}"));
            continue;
        }
        // Speed, for the meld solver's budget: stat computation plus profile evaluation.
        var (benchMeta, benchSet) = sets[0];
        var benchInput = GearsetInputs.FromBis(benchMeta, benchSet);
        var benchMedicine = RotationRegistry.For(job)!.UsesPotion ? engine.BestMedicine(job) : null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 2000;
        for (var i = 0; i < iterations; i++)
        {
            var st = engine.Compute(benchInput);
            model.Evaluate(st, benchMedicine != null ? engine.WithMedicine(st, benchMedicine) : null);
        }
        var perSet = watch.Elapsed.TotalMicroseconds / iterations;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"          {job}: {sets.Count} sets, worst {worst:0.000%}, {perSet:0} µs per set evaluated"));
    }
}

static double MeanDps(StatEngine engine, string job, BisMeta meta, BisSet set, List<double[]> runs)
    => runs.Average(r => Simulation.Run(engine, GearsetInputs.FromBis(meta, set), RotationRegistry.For(job, RotationRegistry.DefaultPotencyDirectory)!,
        new SimSettings { FightSeconds = r[1] }).Dps);

// Hand-checkable runs of a scripted rotation on a real BiS set: GCD counts, charges,
// damage-over-time ticks, forced crits and haste.
static IEnumerable<string> SimMechanics(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    var (_, sheet) = bis.First(b => b.Sheet.Meta.Job == "DRG");
    var stats = engine.Compute(GearsetInputs.FromBis(sheet.Meta, sheet.Sets[0]));
    var gcdMs = stats.GcdFor(stats.Final[Stats.SkillSpeed]) * 10;
    SimResult Run(TestRotation r, int seconds)
        => new Simulation(stats, null, 30_000, r, new SimSettings { FightSeconds = seconds, UsePotion = false }).Run();
    int GcdsIn(int ms) => (ms + gcdMs - 1) / gcdMs;

    var oneHit = DamageMath.BaseDamage(stats, 100, AttackType.Weaponskill, false, false, 100)
                 * DamageMath.CritDirectHitFactor(stats, 0, 0, false, false);

    var plain = Run(new TestRotation(), 30);
    if (plain.GcdCount != GcdsIn(30_000))
        yield return $"30s of a {gcdMs}ms GCD: {plain.GcdCount} GCDs, expected {GcdsIn(30_000)}";
    if (Math.Abs(plain.ByAbility["Strike"].Damage - oneHit * GcdsIn(30_000)) > 1e-6)
        yield return $"damage {plain.ByAbility["Strike"].Damage:0.0} is not {GcdsIn(30_000)} plain hits";

    // Two charges at 0s, then one back every 40s: uses at 0, 0, 40, 80.
    var charged = Run(new TestRotation { UseCharged = true }, 100);
    if (charged.ByAbility.GetValueOrDefault("Charged")?.Uses != 4)
        yield return $"2-charge 40s ability over 100s: {charged.ByAbility.GetValueOrDefault("Charged")?.Uses} uses, expected 4";

    // Applied at 0s for 24s; server ticks every 3s from 3s: 8 ticks.
    var dotted = Run(new TestRotation { ApplyDotOnce = true }, 60);
    var tick = DamageMath.BaseDamage(stats, 50, AttackType.Weaponskill, false, true, 100) * DamageMath.CritDirectHitFactor(stats, 0, 0, false, false);
    var dotDamage = dotted.ByAbility.GetValueOrDefault("Test DoT")?.Damage ?? 0;
    if (Math.Abs(dotDamage - 8 * tick) > 1e-6)
        yield return $"24s DoT: {dotDamage / tick:0.##} ticks of damage, expected 8";

    var forced = Run(new TestRotation { ForceOnce = true }, 10);
    var crit = DamageMath.BaseDamage(stats, 100, AttackType.Weaponskill, false, false, 100) * DamageMath.CritDirectHitFactor(stats, 0, 0, true, false);
    if (Math.Abs(forced.ByAbility["Strike"].Damage - (crit + oneHit * (GcdsIn(10_000) - 1))) > 1e-6)
        yield return "a forced crit was not applied to exactly one hit";

    var hasted = Run(new TestRotation { HasteBuff = true }, 30);
    if (hasted.GcdCount <= plain.GcdCount)
        yield return $"a 20% haste buff didn't add GCDs ({hasted.GcdCount} vs {plain.GcdCount})";
}

// Every job with a rotation sim and xivgear results: absolute within 2% of xivgear on the
// sheet's first set, and every other set within 0.3% after scaling to it. Speed variants (the
// all-SkS/SpS re-melds, or anything that moves the GCD count by over 1%) get 2%: both sims are
// single deterministic timelines, so where the last burst falls against the fight's end moves by
// whole actions. Where xivgear was also run at seven lengths (420-540s) the averaged ratio replaces
// the single-length one, still at 2%: xivgear's scripted PLD, RPR and SAM sims gain 1.1-1.8% less
// from SkS than ours even averaged (PLD's drifts Fight or Flight ~2s a minute at faster GCDs; SAM
// switches to a different hand-written rotation per GCD tier), the other jobs agree within 0.9%.
static IEnumerable<string> SimCalibration(StatEngine engine, List<(string File, BisSheet Sheet)> bis)
{
    var simDir = Path.Combine("data", "reference", "xivgear", "sims");
    var jobsChecked = 0;
    foreach (var file in Directory.Exists(simDir) ? Directory.EnumerateFiles(simDir, "*.json").Order(StringComparer.Ordinal).ToList() : [])
    {
        var sims = GameDataJson.Read<SimReference>(file);
        if (RotationRegistry.For(sims.Job) == null)
        {
            Console.WriteLine($"          {sims.Job}: no rotation sim yet");
            continue;
        }
        var sets = bis.Where(b => b.Sheet.Meta.Job == sims.Job).SelectMany(b => b.Sheet.Sets.Select(set => (b.Sheet.Meta, set))).ToList();
        var variantPath = Path.Combine("data", "reference", "xivgear", "variants", $"{sims.Job}.json");
        if (File.Exists(variantPath))
        {
            var variants = GameDataJson.Read<BisSheet>(variantPath);
            sets.AddRange(variants.Sets.Where(v => v.Name.StartsWith("RMC ", StringComparison.Ordinal)).Select(v => (variants.Meta, v)));
        }

        var pairs = new List<(string Name, double Ours, double Ref, int Gcds)>();
        foreach (var r in sims.Sets)
        {
            var match = sets.FirstOrDefault(x => x.set.Name.Trim() == r.Name.Trim());
            if (match.set == null)
            {
                yield return $"{sims.Job}: sim result for unknown set '{r.Name}'";
                continue;
            }
            var result = Simulation.Run(engine, GearsetInputs.FromBis(match.Meta, match.set), RotationRegistry.For(sims.Job, RotationRegistry.DefaultPotencyDirectory)!,
                new SimSettings { FightSeconds = r.Seconds ?? sims.FightSeconds });
            pairs.Add((r.Name, result.Dps, r.Dps, result.GcdCount));
        }
        if (pairs.Count == 0)
            continue;
        jobsChecked++;
        var absolute = pairs[0].Ours / pairs[0].Ref - 1;
        if (Math.Abs(absolute) > 0.02)
            yield return string.Create(CultureInfo.InvariantCulture, $"{sims.Job}: absolute {absolute:+0.00%;-0.00%} vs xivgear");
        var worst = 0.0;
        var worstSpeed = 0.0;
        // Sets xivgear was also run for at seven fight lengths: compare the length-averaged
        // ratio to the first set instead, which cancels where the last burst falls.
        var averaged = new Dictionary<string, double>(StringComparer.Ordinal);
        var baseName = sims.Sets[0].Name.Trim();
        if (sims.LengthRuns.TryGetValue(baseName, out var baseRuns))
        {
            var baseSet = sets.First(x => x.set.Name.Trim() == baseName);
            var oursBase = MeanDps(engine, sims.Job, baseSet.Meta, baseSet.set, baseRuns);
            var refBase = baseRuns.Average(r => r[0]);
            foreach (var (name, runs) in sims.LengthRuns.Where(kv => kv.Key != baseName))
            {
                var match = sets.First(x => x.set.Name.Trim() == name);
                var err = MeanDps(engine, sims.Job, match.Meta, match.set, runs) / oursBase / (runs.Average(r => r[0]) / refBase) - 1;
                averaged[name] = err;
                if (Math.Abs(err) > 0.02)
                    yield return string.Create(CultureInfo.InvariantCulture, $"{sims.Job} '{name}': {err:+0.00%;-0.00%} relative, averaged over {runs.Count} fight lengths");
            }
        }
        foreach (var (name, ours, reference, gcds) in pairs)
        {
            if (averaged.ContainsKey(name))
                continue;
            var err = ours / (1 + absolute) / reference - 1;
            var speedVariant = name is "RMC all sks" or "RMC all sps" || Math.Abs(gcds - pairs[0].Gcds) > pairs[0].Gcds / 100.0;
            if (speedVariant) worstSpeed = Math.Max(worstSpeed, Math.Abs(err));
            else worst = Math.Max(worst, Math.Abs(err));
            if (Math.Abs(err) > (speedVariant ? 0.02 : 0.003))
                yield return string.Create(CultureInfo.InvariantCulture, $"{sims.Job} '{name}': {err:+0.00%;-0.00%} relative (ours {ours:0.0}, xivgear {reference:0.0})");
        }
        var averagedNote = averaged.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $", length-averaged {averaged.Values.Max(Math.Abs):0.00%}") : "";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"          {sims.Job}: {pairs.Count} sets, absolute {absolute:+0.00%;-0.00%}, worst relative {worst:0.00%}, speed variants {worstSpeed:0.00%}") + averagedNote);
    }
    if (jobsChecked == 0)
        yield return "no job has both a rotation sim and xivgear results";
}

/// <summary>Scripted rotation for the mechanics checks: one 100-potency GCD on repeat, plus switches.</summary>
internal sealed class TestRotation : Rotation
{
    public bool UseCharged { get; init; }
    public bool ApplyDotOnce { get; init; }
    public bool ForceOnce { get; init; }
    public bool HasteBuff { get; init; }

    private readonly SimAbility strike = Gcd("Strike", 0, 100);
    private readonly SimAbility dotter = Gcd("Dotter", 0, 0, dot: new SimDot { Name = "Test DoT", Potency = 50, DurationMs = 24_000 });
    private readonly SimAbility charged = Ogcd("Charged", 0, 0, 40_000, charges: 2);
    private readonly SimAbility force = Ogcd("Force", 0, 0, 1_000_000, grants: ["Force"]);
    private readonly SimAbility haste = Ogcd("Haste", 0, 0, 1_000_000, grants: ["Haste"]);
    private bool dotApplied;

    public override string Job => "DRG";
    public override int AutoAttackPotency => 0;
    public override IReadOnlyList<SimAbility> Abilities => [strike, dotter, charged, force, haste];
    public override IReadOnlyList<SimBuff> Buffs =>
    [
        new() { Name = "Force", DurationMs = 60_000, ForceCrit = true },
        new() { Name = "Haste", DurationMs = 600_000, HastePercent = 20 },
    ];
    public override void Reset() => dotApplied = false;
    public override SimAbility? NextGcd(SimState s) => ApplyDotOnce ? (dotApplied ? null : dotter) : strike;
    public override SimAbility? NextOgcd(SimState s)
        => UseCharged && s.IsReady(charged) ? charged
         : ForceOnce && s.IsReady(force) ? force
         : HasteBuff && s.IsReady(haste) ? haste
         : null;
    public override void OnUse(SimAbility a, SimState s)
    {
        if (a == dotter)
            dotApplied = true;
    }
}

internal sealed class SimReference
{
    public string Job { get; set; } = string.Empty;
    public string Sim { get; set; } = string.Empty;
    public int FightSeconds { get; set; } = 510;
    public List<SimRefSet> Sets { get; set; } = [];
    /// <summary>Set name → xivgear's [DPS, simulated seconds] at fight lengths 420–540s.</summary>
    public Dictionary<string, List<double[]>> LengthRuns { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class SimRefSet
{
    public string Name { get; set; } = string.Empty;
    public double Dps { get; set; }
    /// <summary>The length xivgear actually simulated, when its rotation script doesn't stop at the fight length.</summary>
    public double? Seconds { get; set; }
}

internal sealed class ComputedRef
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, double> Stats { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class PanelObservation
{
    public GearsetInput Gear { get; set; } = new();
    public Dictionary<string, int> Panel { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class Checks
{
    private int failed;

    public void Run(string name, Func<IEnumerable<string>> check)
    {
        List<string> problems;
        try
        {
            problems = check().ToList();
        }
        catch (Exception ex)
        {
            problems = [$"threw {ex.GetType().Name}: {ex.Message}"];
        }

        if (problems.Count == 0)
        {
            Console.WriteLine($"  PASS  {name}");
            return;
        }

        failed++;
        Console.WriteLine($"  FAIL  {name} ({problems.Count})");
        foreach (var p in problems.Take(20))
            Console.WriteLine($"          {p}");
        if (problems.Count > 20)
            Console.WriteLine($"          ... and {problems.Count - 20} more");
    }

    public int Finish()
    {
        Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} check(s) failed.");
        return failed == 0 ? 0 : 1;
    }
}

internal sealed class WeeklyIncomeFile
{
    public Dictionary<string, int> PerWeek { get; set; } = new(StringComparer.Ordinal);
    public string HuntCurrency { get; set; } = "Sack of Nuts";
}
