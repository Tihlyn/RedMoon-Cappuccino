using Rmc.GameData;

// Phase 1 gate: is the game data snapshot complete, does it agree with xivgear's data, and are
// our BiS sheets legal against it? Run from server/.

var snapshotPath = args.Length > 0 ? args[0] : LatestSnapshot(Path.Combine("data", "gamedata"));
var bisDir = Path.Combine("data", "bis");
var referenceDir = Path.Combine("data", "reference", "xivgear");

var snapshot = GameDataJson.ReadSnapshot(snapshotPath);
var data = new GameDataIndex(snapshot);
Console.WriteLine($"Snapshot {snapshotPath}: patch {snapshot.Meta.Patch}, game {snapshot.Meta.GameVersion}, {snapshot.Items.Count} items");

var checks = new Checks();
checks.Run("levels", () => CheckLevels(snapshot));
checks.Run("jobs", () => CheckJobs(snapshot));
checks.Run("base params", () => CheckBaseParams(snapshot));
checks.Run("items", () => CheckItems(snapshot));
checks.Run("materia", () => CheckMateria(snapshot));
checks.Run("food", () => CheckFood(snapshot));
checks.Run("meld caps (primary substat at cap)", () => CheckCapInvariant(data));
checks.Run("item sources", () => CheckSources(data, bisDir));
checks.Run("xivgear reference", () => CheckReference(data, referenceDir));
checks.Run("BiS sheets", () => CheckBis(data, bisDir));
return checks.Finish();

static string LatestSnapshot(string dir)
    => Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault()
       ?? throw new FileNotFoundException($"No snapshot in {dir}. Run rmc-extract extract first.");

static IEnumerable<string> CheckLevels(GameDataSnapshot s)
{
    if (s.Levels.All(l => l.Level != s.Meta.MaxLevel))
        yield return $"no level scaling for max level {s.Meta.MaxLevel}";
    foreach (var l in s.Levels)
    {
        if (l.LevelDiv <= 0 || l.BaseMainStat <= 0 || l.BaseSubStat <= 0)
            yield return $"level {l.Level}: missing values";
        // The base substat lives in overlays/levels.json; ParamGrow's BaseSpeed is the same number.
        if (l.BaseSubStat != l.BaseSpeed)
            yield return $"level {l.Level}: overlay base substat {l.BaseSubStat} != ParamGrow BaseSpeed {l.BaseSpeed}";
    }
}

static IEnumerable<string> CheckJobs(GameDataSnapshot s)
{
    var expected = new Dictionary<string, string>
    {
        ["PLD"] = "Tank", ["WAR"] = "Tank", ["DRK"] = "Tank", ["GNB"] = "Tank",
        ["WHM"] = "Healer", ["SCH"] = "Healer", ["AST"] = "Healer", ["SGE"] = "Healer",
        ["MNK"] = "Melee", ["DRG"] = "Melee", ["NIN"] = "Melee", ["SAM"] = "Melee", ["RPR"] = "Melee", ["VPR"] = "Melee",
        ["BRD"] = "Ranged", ["MCH"] = "Ranged", ["DNC"] = "Ranged",
        ["BLM"] = "Caster", ["SMN"] = "Caster", ["RDM"] = "Caster", ["PCT"] = "Caster",
    };
    var byAbbr = s.ClassJobs.ToDictionary(j => j.Abbreviation);
    foreach (var (abbr, role) in expected)
    {
        if (!byAbbr.TryGetValue(abbr, out var job))
            yield return $"{abbr} missing";
        else if (job.Role != role)
            yield return $"{abbr}: role {job.Role}, expected {role}";
        else if (job.Limited)
            yield return $"{abbr} flagged as a limited job";
        else if (job.Modifiers.GetValueOrDefault(job.MainStat) < 100)
            yield return $"{abbr}: main stat modifier {job.Modifiers.GetValueOrDefault(job.MainStat)} looks wrong";
    }
}

static IEnumerable<string> CheckBaseParams(GameDataSnapshot s)
{
    var capSlots = typeof(CapSlots).GetFields().Select(f => (string)f.GetValue(null)!).ToArray();
    foreach (var key in Stats.ByBaseParamId.Values)
    {
        var bp = s.BaseParams.FirstOrDefault(b => b.Key == key);
        if (bp == null)
        {
            yield return $"{key} missing";
            continue;
        }
        if (bp.MeldParam.Count == 0)
            yield return $"{key}: no meld params";
        foreach (var slot in capSlots.Where(c => !bp.SlotPercent.ContainsKey(c)))
            yield return $"{key}: no {slot} percentage";
    }
}

static IEnumerable<string> CheckItems(GameDataSnapshot s)
{
    var ilvls = s.ItemLevels.Select(l => l.ItemLevel).ToHashSet();
    var meldParamCount = s.BaseParams.Min(b => b.MeldParam.Count);
    var slotKeys = Slots.All.Append(Slots.Ring).ToHashSet();
    foreach (var i in s.Items)
    {
        if (!ilvls.Contains(i.ItemLevel))
            yield return $"{i.Id} {i.Name}: item level {i.ItemLevel} has no ItemLevel row";
        if (!slotKeys.Contains(i.Slot) || string.IsNullOrEmpty(i.CapSlot))
            yield return $"{i.Id} {i.Name}: slot {i.Slot}/{i.CapSlot}";
        if (i.Jobs.Count == 0)
            yield return $"{i.Id} {i.Name}: no jobs";
        if (i.MateriaSlots is < 0 or > GearRules.MaxMateriaSlots)
            yield return $"{i.Id} {i.Name}: {i.MateriaSlots} materia slots";
        if (i.MeldParamIndex >= meldParamCount)
            yield return $"{i.Id} {i.Name}: meld param index {i.MeldParamIndex} out of range";
        if (i.Slot == Slots.Weapon && (i.Weapon == null || i.Weapon.DelayMs <= 0))
            yield return $"{i.Id} {i.Name}: weapon without damage/delay";
        if (i.StatsHq.Any(kv => kv.Value < i.Stats.GetValueOrDefault(kv.Key)))
            yield return $"{i.Id} {i.Name}: HQ stats below NQ";
    }
}

static IEnumerable<string> CheckMateria(GameDataSnapshot s)
{
    foreach (var stat in Stats.CombatSubstats)
    {
        var m = s.Materia.FirstOrDefault(x => x.Stat == stat);
        if (m == null)
        {
            yield return $"no {stat} materia";
            continue;
        }
        var top = m.Grades.FirstOrDefault(g => g.Grade == GearRules.MaxGrade);
        var overmeld = m.Grades.FirstOrDefault(g => g.Grade == GearRules.MaxGrade - 1);
        if (top == null || overmeld == null)
            yield return $"{stat}: grades XI/XII missing";
        else if (top.Value <= overmeld.Value)
            yield return $"{stat}: XII ({top.Value}) not above XI ({overmeld.Value})";
    }
}

static IEnumerable<string> CheckFood(GameDataSnapshot s)
{
    if (s.Foods.Count == 0)
        yield return "no food";
    foreach (var f in s.Foods)
    {
        foreach (var b in f.Bonuses)
        {
            if (b.Value <= 0 || (b.Relative && b.Max <= 0))
                yield return $"{f.ItemId} {f.Name}: {b.Stat} bonus {b.Value}/{b.Max}";
            if (f.CanBeHq && (b.ValueHq < b.Value || b.MaxHq < b.Max))
                yield return $"{f.ItemId} {f.Name}: HQ {b.Stat} bonus below NQ";
        }
    }
}

// The game gives every piece its main substat exactly at the meld cap. Checking that against
// our cap formula exercises BaseParam, ItemLevel, the meld param index and the slot shape mapping
// on every item, with no outside reference.
static IEnumerable<string> CheckCapInvariant(GameDataIndex data)
{
    var checkedItems = 0;
    foreach (var item in data.Snapshot.Items)
    {
        var stats = item.CanBeHq ? item.StatsHq : item.Stats;
        var substats = stats.Where(kv => Stats.CombatSubstats.Contains(kv.Key)).ToList();
        if (substats.Count < 2)
            continue;
        var primary = substats.MaxBy(kv => kv.Value);
        var cap = data.MeldCap(item, primary.Key);
        checkedItems++;
        if (cap != primary.Value)
            yield return $"{item.Id} {item.Name} (i{item.ItemLevel} {item.CapSlot}): {primary.Key} {primary.Value}, computed cap {cap?.ToString() ?? "none"}";
        foreach (var (stat, value) in substats)
        {
            if (data.MeldCap(item, stat) is int c && value > c)
                yield return $"{item.Id} {item.Name}: {stat} {value} above computed cap {c}";
        }
    }
    if (checkedItems == 0)
        yield return "no items with two substats to check";
}

// Sources are derived from recipes and exchange shops. A new tier's currency that
// overlays/currencies.json doesn't name, or current-tier gear left Unknown, fails here.
static IEnumerable<string> CheckSources(GameDataIndex data, string bisDir)
{
    var s = data.Snapshot;
    foreach (var currency in s.Meta.UnclassifiedCurrencies)
        yield return $"currency '{currency}' matches no rule in overlays/currencies.json";

    // The current tier spans about 25 item levels (normal raid to savage weapon).
    var tierFloor = s.Items.Max(i => i.ItemLevel) - 25;
    foreach (var i in s.Items.Where(i => i.ItemLevel >= tierFloor && i.Source.Type == SourceTypes.Unknown))
        yield return $"{i.Id} {i.Name} (i{i.ItemLevel}): current-tier item with no known source";

    foreach (var i in s.Items.Where(i => i.Source.Augment is { } a && data.Item(a.BaseItemId) == null && a.BaseItemId != 0))
        yield return $"{i.Id} {i.Name}: augment base {i.Source.Augment!.BaseItemId} is not in the snapshot";

    if (!Directory.Exists(bisDir))
        yield break;
    var bisItems = Directory.EnumerateFiles(bisDir, "*.json", SearchOption.AllDirectories)
        .SelectMany(f => GameDataJson.Read<BisSheet>(f).Sets.SelectMany(set => set.Slots.Values.Select(v => v.ItemId)))
        .ToHashSet();
    foreach (var id in bisItems)
    {
        if (data.Item(id) is { } item && item.Source.Type is SourceTypes.Unknown or SourceTypes.Other)
            yield return $"BiS item {id} {item.Name}: source {item.Source.Type}";
    }
}

static IEnumerable<string> CheckReference(GameDataIndex data, string dir)
{
    if (!Directory.Exists(dir))
    {
        yield return $"no reference snapshot at {dir} (run rmc-extract snapshot-xivgear)";
        yield break;
    }
    var s = data.Snapshot;

    foreach (var r in GameDataJson.Read<List<BaseParamInfo>>(Path.Combine(dir, "baseparams.json")))
    {
        var ours = s.BaseParams.FirstOrDefault(b => b.Id == r.Id);
        if (ours == null)
        {
            yield return $"base param {r.Key} missing";
            continue;
        }
        if (!ours.MeldParam.SequenceEqual(r.MeldParam))
            yield return $"base param {r.Key}: meld params [{string.Join(',', ours.MeldParam)}] vs xivgear [{string.Join(',', r.MeldParam)}]";
        foreach (var (slot, pct) in r.SlotPercent.Where(kv => ours.SlotPercent.GetValueOrDefault(kv.Key) != kv.Value))
            yield return $"base param {r.Key}: {slot} {ours.SlotPercent.GetValueOrDefault(slot)}% vs xivgear {pct}%";
    }

    foreach (var r in GameDataJson.Read<List<ItemLevelInfo>>(Path.Combine(dir, "itemlevels.json")))
    {
        var ours = data.ItemLevel(r.ItemLevel);
        if (ours == null)
        {
            yield return $"item level {r.ItemLevel} missing";
            continue;
        }
        foreach (var (stat, value) in r.Stats.Where(kv => ours.Stats.GetValueOrDefault(kv.Key) != kv.Value))
            yield return $"item level {r.ItemLevel}: {stat} {ours.Stats.GetValueOrDefault(stat)} vs xivgear {value}";
    }

    foreach (var r in GameDataJson.Read<List<MateriaInfo>>(Path.Combine(dir, "materia.json")))
    {
        var ours = s.Materia.FirstOrDefault(m => m.Stat == r.Stat);
        foreach (var g in r.Grades)
        {
            var mine = ours?.Grades.FirstOrDefault(x => x.Grade == g.Grade);
            if (mine == null || mine.ItemId != g.ItemId || mine.Value != g.Value)
                yield return $"materia {r.Stat} grade {g.Grade}: ours {mine?.ItemId}/{mine?.Value} vs xivgear {g.ItemId}/{g.Value}";
        }
    }

    foreach (var r in GameDataJson.Read<List<GearItem>>(Path.Combine(dir, "items.json")))
    {
        var ours = data.Item(r.Id);
        if (ours == null)
        {
            yield return $"BiS item {r.Id} {r.Name} missing";
            continue;
        }
        var where = $"item {r.Id} {r.Name}";
        if (ours.ItemLevel != r.ItemLevel) yield return $"{where}: ilvl {ours.ItemLevel} vs {r.ItemLevel}";
        if (ours.MateriaSlots != r.MateriaSlots) yield return $"{where}: materia slots {ours.MateriaSlots} vs {r.MateriaSlots}";
        if (ours.AdvancedMelding != r.AdvancedMelding) yield return $"{where}: advanced melding {ours.AdvancedMelding} vs {r.AdvancedMelding}";
        if (ours.MeldParamIndex != r.MeldParamIndex) yield return $"{where}: meld param index {ours.MeldParamIndex} vs {r.MeldParamIndex}";
        if (ours.CanBeHq != r.CanBeHq) yield return $"{where}: can be HQ {ours.CanBeHq} vs {r.CanBeHq}";
        if (ours.Unique != r.Unique) yield return $"{where}: unique {ours.Unique} vs {r.Unique}";
        foreach (var d in StatDiff(ours.Stats, r.Stats))
            yield return $"{where}: NQ {d}";
        if (r.CanBeHq)
            foreach (var d in StatDiff(ours.StatsHq, r.StatsHq))
                yield return $"{where}: HQ {d}";
        if (r.Weapon != null)
        {
            var w = ours.Weapon;
            if (w == null || w.PhysicalDamage != r.Weapon.PhysicalDamage || w.MagicalDamage != r.Weapon.MagicalDamage || w.DelayMs != r.Weapon.DelayMs)
                yield return $"{where}: weapon {w?.PhysicalDamage}/{w?.MagicalDamage}/{w?.DelayMs} vs {r.Weapon.PhysicalDamage}/{r.Weapon.MagicalDamage}/{r.Weapon.DelayMs}";
            else if (r.CanBeHq && (w.PhysicalDamageHq != r.Weapon.PhysicalDamageHq || w.MagicalDamageHq != r.Weapon.MagicalDamageHq))
                yield return $"{where}: HQ weapon {w.PhysicalDamageHq}/{w.MagicalDamageHq} vs {r.Weapon.PhysicalDamageHq}/{r.Weapon.MagicalDamageHq}";
        }
    }
}

static IEnumerable<string> StatDiff(IReadOnlyDictionary<string, int> ours, IReadOnlyDictionary<string, int> theirs)
    => ours.Keys.Union(theirs.Keys)
        .Where(k => ours.GetValueOrDefault(k) != theirs.GetValueOrDefault(k))
        .Select(k => $"{k} {ours.GetValueOrDefault(k)} vs {theirs.GetValueOrDefault(k)}");

static IEnumerable<string> CheckBis(GameDataIndex data, string dir)
{
    var sheets = Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Select(f => (f, sheet: GameDataJson.Read<BisSheet>(f))).ToList()
        : [];
    foreach (var job in data.Snapshot.ClassJobs.Where(j => !j.Limited))
    {
        if (sheets.All(x => !string.Equals(x.sheet.Meta.Job, job.Abbreviation, StringComparison.OrdinalIgnoreCase)))
            yield return $"{job.Abbreviation}: no BiS sheet";
    }
    foreach (var (file, sheet) in sheets)
    {
        if (sheet.Sets.Count == 0)
            yield return $"{file}: no sets";
        foreach (var set in sheet.Sets)
        {
            if (set.Slots.Count < 11)
                yield return $"{file} {set.Name}: only {set.Slots.Count} slots";
            foreach (var issue in BisValidator.Validate(data, sheet.Meta.Job, set).Where(i => i.IsError))
                yield return $"{file}: {issue.Message}";
        }
    }
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
        foreach (var p in problems.Take(15))
            Console.WriteLine($"          {p}");
        if (problems.Count > 15)
            Console.WriteLine($"          ... and {problems.Count - 15} more");
    }

    public int Finish()
    {
        Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} check(s) failed.");
        return failed == 0 ? 0 : 1;
    }
}
