using Rmc.GameData;
using Rmc.GameData.Extractor;

// rmc-extract: builds the server's game data from the local game files and imports BiS sheets.
// Paths default to the server/ layout; run from the server/ directory.

const string DefaultSqpack = @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 1;
}

var command = args[0];
var opts = ParseOptions(args.Skip(1));

try
{
    switch (command)
    {
        case "extract":
        {
            var patch = Require(opts, "patch");
            var extractor = new LuminaExtractor(new ExtractOptions
            {
                SqpackPath = opts.GetValueOrDefault("game", DefaultSqpack),
                Patch = patch,
                OverlayDirectory = opts.GetValueOrDefault("overlays", Path.Combine("data", "overlays")),
                Level = int.Parse(opts.GetValueOrDefault("level", "0")),
                MinItemLevel = int.Parse(opts.GetValueOrDefault("min-ilvl", "0")),
                MinFoodItemLevel = int.Parse(opts.GetValueOrDefault("min-food-ilvl", "600")),
            }, Console.Out);
            var snapshot = extractor.Extract();
            var output = opts.GetValueOrDefault("out", Path.Combine("data", "gamedata", $"{patch}.json"));
            GameDataJson.Write(output, snapshot);
            Console.WriteLine($"Wrote {output}");
            return 0;
        }
        case "import-bis":
        {
            var data = new GameDataIndex(GameDataJson.ReadSnapshot(Require(opts, "gamedata")));
            var importer = new BisImporter(data, Console.Out);
            var jobs = opts.TryGetValue("jobs", out var list)
                ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : data.Snapshot.ClassJobs.Select(j => j.Abbreviation).ToArray();
            var sheet = opts.GetValueOrDefault("sheet", "current");
            var outRoot = opts.GetValueOrDefault("out", Path.Combine("data", "bis"));

            // --refresh re-imports every committed sheet from the source recorded in its meta.
            if (opts.ContainsKey("refresh"))
            {
                var refreshFailures = 0;
                foreach (var file in Directory.EnumerateFiles(outRoot, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var existing = GameDataJson.Read<BisSheet>(file);
                    var src = existing.Meta.Source;
                    var refreshed = src.Kind switch
                    {
                        "xivgear-static" => await importer.ImportStaticAsync(existing.Meta.Job, Path.GetFileNameWithoutExtension(src.Reference)),
                        "xivgear-api" => await importer.ImportUrlsAsync(existing.Meta.Job, src.Reference.Split(',')),
                        _ => null,
                    };
                    if (refreshed == null)
                    {
                        Console.WriteLine($"{file}: not refreshed (source '{src.Kind}').");
                        refreshFailures++;
                        continue;
                    }
                    GameDataJson.Write(file, refreshed);
                    Console.WriteLine($"Refreshed {file} ({refreshed.Sets.Count} sets)");
                }
                return refreshFailures == 0 ? 0 : 2;
            }

            // --url imports one job from xivgear links (comma-separated; their sets are merged).
            if (opts.TryGetValue("url", out var urls))
            {
                if (jobs.Length != 1)
                    throw new ArgumentException("--url needs exactly one job in --jobs.");
                var imported = await importer.ImportUrlsAsync(jobs[0], urls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (imported == null)
                    return 2;
                var urlPath = Path.Combine(outRoot, jobs[0].ToUpperInvariant(), $"{sheet.Replace('/', '_')}.json");
                GameDataJson.Write(urlPath, imported);
                Console.WriteLine($"Wrote {urlPath} ({imported.Sets.Count} sets)");
                return 0;
            }

            // Limited jobs (BLU, BST) have no raid BiS; skip them unless asked for by name.
            if (!opts.ContainsKey("jobs"))
                jobs = data.Snapshot.ClassJobs.Where(j => !j.Limited).Select(j => j.Abbreviation).ToArray();

            var failures = 0;
            foreach (var job in jobs)
            {
                var result = await importer.ImportStaticAsync(job, sheet);
                if (result == null)
                {
                    failures++;
                    continue;
                }
                var path = Path.Combine(outRoot, job.ToUpperInvariant(), $"{sheet.Replace('/', '_')}.json");
                GameDataJson.Write(path, result);
                Console.WriteLine($"Wrote {path} ({result.Sets.Count} sets)");
            }
            return failures == 0 ? 0 : 2;
        }
        case "calibration-variants":
        {
            var data = new GameDataIndex(GameDataJson.ReadSnapshot(Require(opts, "gamedata")));
            await new CalibrationVariants(data, Console.Out).WriteAsync(
                opts.GetValueOrDefault("bis", Path.Combine("data", "bis")),
                opts.GetValueOrDefault("out", Path.Combine("data", "reference", "xivgear", "variants")));
            return 0;
        }
        case "refresh-potencies":
        {
            // Patch day: read every rotation ability's potency from the tooltips into data/potencies/<JOB>.json.
            var game = new Lumina.GameData(opts.GetValueOrDefault("game", DefaultSqpack), new Lumina.LuminaOptions());
            var gameVersionFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.GetValueOrDefault("game", DefaultSqpack)).TrimEnd('\\', '/'))!, "ffxivgame.ver");
            var reader = new ActionPotencies(game);
            var outDir = opts.GetValueOrDefault("out", Path.Combine("data", "potencies"));
            var problems = 0;
            foreach (var job in Rmc.Engine.Sim.Rotations.RotationRegistry.Jobs.Order())
            {
                var rotation = Rmc.Engine.Sim.Rotations.RotationRegistry.For(job)!;
                var jobRow = game.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()!.First(j => j.Abbreviation.ExtractText() == job);
                var previous = Rmc.Engine.Sim.PotencyTable.Load(outDir, job);
                var table = new Rmc.Engine.Sim.PotencyTable
                {
                    Job = job,
                    GameVersion = File.Exists(gameVersionFile) ? File.ReadAllText(gameVersionFile).Trim() : "unknown",
                };
                var sources = rotation.Abilities.Where(a => a.ActionId != 0 && a.Potency > 0 && a.PotencyLabel != "manual")
                    .Select(a => (a.Name, a.ActionId, a.PotencyLabel, Default: a.Potency))
                    .Concat(rotation.Abilities.Where(a => a.Dot is { ActionId: not 0 } d && d.PotencyLabel != "manual")
                        .Select(a => (a.Dot!.Name, a.Dot.ActionId, a.Dot.PotencyLabel, Default: a.Dot.Potency)))
                    .DistinctBy(x => (x.ActionId, x.PotencyLabel));
                foreach (var (name, id, label, fallback) in sources)
                {
                    var info = reader.Get(id, (int)jobRow.RowId, 100);
                    var found = info?.Potencies.FirstOrDefault(p => p.Label == label);
                    if (found == null)
                    {
                        Console.WriteLine($"{job} {name} ({id}): tooltip has no '{label}' potency; keeping {fallback}. Fix the rotation's label.");
                        problems++;
                        table.Entries.Add(new Rmc.Engine.Sim.PotencyEntry { Name = name, ActionId = id, Label = label, Potency = fallback });
                        continue;
                    }
                    var old = previous?.Entries.FirstOrDefault(e => e.ActionId == id && e.Label == label)?.Potency ?? fallback;
                    if (old != found.Value)
                        Console.WriteLine($"{job} {name}: {old} -> {found.Value}");
                    table.Entries.Add(new Rmc.Engine.Sim.PotencyEntry { Name = name, ActionId = id, Label = label, Potency = found.Value });
                }
                GameDataJson.Write(Path.Combine(outDir, $"{job}.json"), table);
                Console.WriteLine($"Wrote {job}: {table.Entries.Count} potencies");
            }
            return problems == 0 ? 0 : 2;
        }
        case "potencies":
        {
            // Authoring aid: every action a job has at a level, with the potencies its tooltip shows.
            var game = new Lumina.GameData(opts.GetValueOrDefault("game", DefaultSqpack), new Lumina.LuminaOptions());
            var jobRow = game.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()!
                .First(j => j.Abbreviation.ExtractText().Equals(Require(opts, "job"), StringComparison.OrdinalIgnoreCase));
            var lvl = int.Parse(opts.GetValueOrDefault("level", "100"));
            var reader = new ActionPotencies(game);
            // --find "A,B": look actions up by name (replacements filed under no job, or level 0).
            if (opts.TryGetValue("find", out var names))
            {
                foreach (var a in names.Split(',', StringSplitOptions.TrimEntries).SelectMany(n => reader.Find(n, (int)jobRow.RowId, lvl)))
                {
                    Console.WriteLine($"{a.Id,6} {a.Name,-28} job{a.Job} lv{a.Level,-3} cat{a.Category} {a.Timing} potencies [{string.Join(", ", a.Potencies)}]");
                    if (opts.ContainsKey("text"))
                        Console.WriteLine("       " + a.Text.Replace("\n", "\n       "));
                }
                return 0;
            }
            foreach (var a in reader.ForJob((int)jobRow.RowId, (int)jobRow.ClassJobParent.RowId, lvl).Where(a => a.Potencies.Count > 0 || opts.ContainsKey("all")))
            {
                Console.WriteLine($"{a.Id,6} {a.Name,-28} lv{a.Level,-3} cat{a.Category} {a.Timing} potencies [{string.Join(", ", a.Potencies)}]");
                if (opts.ContainsKey("text"))
                    Console.WriteLine("       " + a.Text.Replace("\n", "\n       "));
            }
            return 0;
        }
        case "sim":
        {
            // Rotation authoring aid: our sim against xivgear's results for a job, with a per-ability breakdown.
            var job = Require(opts, "job").ToUpperInvariant();
            var data = new GameDataIndex(GameDataJson.ReadSnapshot(opts.GetValueOrDefault("gamedata",
                Directory.EnumerateFiles(Path.Combine("data", "gamedata"), "*.json").Order(StringComparer.Ordinal).Last())));
            var engine = new Rmc.Engine.StatEngine(data);
            var sets = Directory.EnumerateFiles(Path.Combine("data", "bis", job), "*.json")
                .Select(GameDataJson.Read<BisSheet>).SelectMany(sh => sh.Sets.Select(set => (sh.Meta, set))).ToList();
            var variantPath = Path.Combine("data", "reference", "xivgear", "variants", $"{job}.json");
            if (File.Exists(variantPath))
            {
                var v = GameDataJson.Read<BisSheet>(variantPath);
                sets.AddRange(v.Sets.Where(x => x.Name.StartsWith("RMC ", StringComparison.Ordinal)).Select(x => (v.Meta, x)));
            }
            var refPath = Path.Combine("data", "reference", "xivgear", "sims", $"{job}.json");
            var refs = File.Exists(refPath)
                ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(refPath)).RootElement.GetProperty("sets").EnumerateArray()
                    .ToDictionary(e => e.GetProperty("name").GetString()!.Trim(), e => e.GetProperty("dps").GetDouble())
                : [];
            var refSeconds = File.Exists(refPath)
                ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(refPath)).RootElement.GetProperty("sets").EnumerateArray()
                    .Where(e => e.TryGetProperty("seconds", out _))
                    .ToDictionary(e => e.GetProperty("name").GetString()!.Trim(), e => e.GetProperty("seconds").GetDouble())
                : [];
            var chosen = refs.Count > 0 ? sets.Where(x => refs.ContainsKey(x.set.Name.Trim())).ToList() : sets.Take(1).ToList();
            var seconds = opts.TryGetValue("seconds", out var sec) ? double.Parse(sec, System.Globalization.CultureInfo.InvariantCulture)
                : File.Exists(refPath) ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(refPath)).RootElement.GetProperty("fightSeconds").GetDouble() : 510;
            // --lengths 420,460,...: every set at each length (no per-set overrides), one row per set.
            if (opts.TryGetValue("lengths", out var lengthList))
            {
                var lengths = lengthList.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                foreach (var (meta, set) in chosen)
                {
                    var dps = lengths.Select(t => Rmc.Engine.Sim.Simulation.Run(engine, Rmc.Engine.GearsetInputs.FromBis(meta, set),
                        Rmc.Engine.Sim.Rotations.RotationRegistry.For(job, Rmc.Engine.Sim.Rotations.RotationRegistry.DefaultPotencyDirectory)!,
                        new Rmc.Engine.Sim.SimSettings { FightSeconds = t }).Dps).ToArray();
                    Console.WriteLine($"{set.Name.Trim(),-34} mean {dps.Average(),9:0.0}  [{string.Join(" ", dps.Select(d => d.ToString("0", System.Globalization.CultureInfo.InvariantCulture)))}]");
                }
                return 0;
            }
            double? scale = null;
            foreach (var (meta, set) in chosen)
            {
                var rotation = Rmc.Engine.Sim.Rotations.RotationRegistry.For(job, Rmc.Engine.Sim.Rotations.RotationRegistry.DefaultPotencyDirectory)
                    ?? throw new ArgumentException($"{job} has no rotation sim.");
                var r = Rmc.Engine.Sim.Simulation.Run(engine, Rmc.Engine.GearsetInputs.FromBis(meta, set), rotation,
                    new Rmc.Engine.Sim.SimSettings { FightSeconds = refSeconds.GetValueOrDefault(set.Name.Trim(), seconds), RecordTimeline = opts.ContainsKey("timeline") });
                if (opts.ContainsKey("timeline") && set == chosen[0].set)
                    foreach (var e in r.Timeline.Where(e => e.Name != "Auto-attack" && !e.Name.EndsWith("(DoT)", StringComparison.Ordinal)))
                        Console.WriteLine($"  {e.TimeMs / 1000.0,7:0.00} {e.Name}");
                var line = $"{set.Name.Trim(),-34} ours {r.Dps,9:0.0}  GCDs {r.GcdCount}";
                if (refs.TryGetValue(set.Name.Trim(), out var reference))
                {
                    scale ??= r.Dps / reference;
                    line += $"  xivgear {reference,9:0.0}  abs {r.Dps / reference - 1,7:+0.00%;-0.00%}  rel {r.Dps / scale / reference - 1,7:+0.00%;-0.00%}";
                }
                Console.WriteLine(line);
                if (set == chosen[0].set || opts.ContainsKey("all"))
                    foreach (var (name, t) in r.ByAbility.OrderByDescending(kv => kv.Value.Damage))
                        Console.WriteLine($"    {name,-28} {t.Uses,4}  {t.Damage / r.TotalDamage,6:0.0%}  avg {(t.Uses > 0 ? t.Damage / t.Uses : 0),9:0}");
            }
            return 0;
        }
        case "solve-melds":
        {
            // Solver authoring aid: re-meld a BiS set from empty and compare with the sheet's melds.
            var job = Require(opts, "job").ToUpperInvariant();
            var data = new GameDataIndex(GameDataJson.ReadSnapshot(Directory.EnumerateFiles(Path.Combine("data", "gamedata"), "*.json").Order(StringComparer.Ordinal).Last()));
            var engine = new Rmc.Engine.StatEngine(data);
            var model = new Rmc.Engine.Damage.FittedProfileModel(GameDataJson.Read<Rmc.Engine.Damage.HitProfile>(Path.Combine("data", "profiles", $"{job}.json")));
            var sheet = GameDataJson.Read<BisSheet>(Path.Combine("data", "bis", job, "current.json"));
            var set = opts.TryGetValue("set", out var setName) ? sheet.Sets.First(s => s.Name.Trim() == setName) : sheet.Sets[0];
            var input = Rmc.Engine.GearsetInputs.FromBis(sheet.Meta, set);
            var options = new Rmc.Engine.Melds.MeldSolveOptions
            {
                SolveFood = opts.ContainsKey("food"),
                KeepExistingMelds = opts.ContainsKey("keep"),
                TargetGcdCs = opts.TryGetValue("gcd", out var gcd) ? (int)Math.Round(double.Parse(gcd, System.Globalization.CultureInfo.InvariantCulture) * 100) : null,
                MinPiety = opts.TryGetValue("piety", out var piety) ? int.Parse(piety) : null,
            };
            var solution = new Rmc.Engine.Melds.MeldSolver(engine, model).Solve(input, options);
            Console.WriteLine($"{job} '{set.Name.Trim()}': sheet melds {solution.CurrentDps:0.0} DPS, solved {solution.Dps:0.0} ({solution.Dps / solution.CurrentDps - 1:+0.00%;-0.00%}), "
                + $"GCD {solution.GcdCs / 100.0:0.00}, {solution.CandidatesScored} candidates, {solution.Elapsed.TotalMilliseconds:0} ms{(solution.Infeasible ? ", INFEASIBLE" : "")}");
            string Melds(Rmc.Engine.PieceInput p) => string.Join(" ", p.Materia.Select(m => $"{m.Stat}{m.Grade}"));
            foreach (var slot in Slots.All.Where(input.Slots.ContainsKey))
                Console.WriteLine($"  {slot,-10} sheet [{Melds(input.Slots[slot]),-28}] solved [{Melds(solution.Gear.Slots[slot])}]");
            Console.WriteLine($"  food       sheet {input.Food?.ItemId} solved {solution.Gear.Food?.ItemId}");
            foreach (var stat in new[] { Stats.Critical, Stats.DirectHit, Stats.Determination, Stats.SkillSpeed, Stats.SpellSpeed, Stats.Tenacity, Stats.Piety })
                Console.WriteLine($"  {stat,-4} {engine.Compute(input).Final.GetValueOrDefault(stat),5} -> {solution.Stats.Final.GetValueOrDefault(stat),5}");
            return 0;
        }
        case "plan-upgrades":
        {
            // Planner authoring aid: from a fresh crafted set (or a BiS set) to a job's BiS, week by week.
            var job = Require(opts, "job").ToUpperInvariant();
            var data = new GameDataIndex(GameDataJson.ReadSnapshot(Directory.EnumerateFiles(Path.Combine("data", "gamedata"), "*.json").Order(StringComparer.Ordinal).Last()));
            var engine = new Rmc.Engine.StatEngine(data);
            var model = new Rmc.Engine.Damage.FittedProfileModel(GameDataJson.Read<Rmc.Engine.Damage.HitProfile>(Path.Combine("data", "profiles", $"{job}.json")));
            var sheet = GameDataJson.Read<BisSheet>(Path.Combine("data", "bis", job, "current.json"));
            var set = opts.TryGetValue("set", out var setName) ? sheet.Sets.First(s => s.Name.Trim() == setName) : sheet.Sets[0];
            var target = Rmc.Engine.GearsetInputs.FromBis(sheet.Meta, set);
            var start = Rmc.Engine.Upgrades.StartingSets.Crafted(engine, model, target);
            var weekly = GameDataJson.Read<WeeklyOverlay>(Path.Combine("data", "overlays", "weekly.json"));
            var plan = new Rmc.Engine.Upgrades.UpgradePlanner(engine, model).Plan(start, target, new Rmc.Engine.Upgrades.UpgradeOptions
            {
                WeeklyIncome = weekly.PerWeek, IncludeCrafted = opts.ContainsKey("crafted"),
                IgnoreWeeklyLimits = opts.ContainsKey("no-limits"), MaterialsFromHunts = opts.ContainsKey("hunts"),
                HuntCurrency = weekly.HuntCurrency,
            });
            Console.WriteLine($"{job} -> '{set.Name.Trim()}': now {plan.CurrentDps:0} DPS (re-melded {plan.CurrentRemeldedDps:0}), BiS {plan.BisDps:0} "
                + $"({plan.CurrentDps / plan.BisDps - 1:+0.0%;-0.0%}); plan ends at {plan.FinalDps:0} "
                + $"{(plan.WeeksToFinish is int w ? $"in week {w}" : "(horizon reached)")}, {plan.Elapsed.TotalMilliseconds:0} ms");
            foreach (var s in plan.Steps)
                Console.WriteLine($"  week {s.Week,2}  {s.Slot,-8} {s.Kind,-7} {s.ToItem} (i{s.ToItemLevel})  {s.Gain,6:+0;-0} DPS ({s.Gain / s.DpsBefore:+0.00%;-0.00%})  {s.CostText}"
                    + (s.RemeldSlots.Count > 0 ? $"  [re-meld {string.Join(",", s.RemeldSlots)}: +{s.RemeldGain:0}]" : ""));
            if (plan.FinalRemeldSlots.Count > 0)
                Console.WriteLine($"  finally   re-meld {string.Join(",", plan.FinalRemeldSlots)}: +{plan.FinalRemeldGain:0} DPS");
            foreach (var d in plan.Drops)
                Console.WriteLine($"  drop     {d.Slot,-8} {d.Item} ({d.SourceType}): +{d.GainIfObtained:0} DPS if obtained");
            foreach (var u in plan.Unreached)
                Console.WriteLine($"  unreached {u}");
            foreach (var u in plan.NoGain)
                Console.WriteLine($"  no DPS gain {u}");
            return 0;
        }
        case "bench-concurrency":
        {
            // Throughput of upgrade plans under different splits of the machine (jobs × threads each).
            var data = new GameDataIndex(GameDataJson.ReadSnapshot(Directory.EnumerateFiles(Path.Combine("data", "gamedata"), "*.json").Order(StringComparer.Ordinal).Last()));
            var engine = new Rmc.Engine.StatEngine(data);
            var weekly = GameDataJson.Read<WeeklyOverlay>(Path.Combine("data", "overlays", "weekly.json"));
            var jobs = (opts.GetValueOrDefault("jobs", "DRG,WHM,PLD,BLM,SAM,MCH,SGE,WAR")).Split(',');
            var requests = jobs.Select(job =>
            {
                var model = new Rmc.Engine.Damage.FittedProfileModel(GameDataJson.Read<Rmc.Engine.Damage.HitProfile>(Path.Combine("data", "profiles", $"{job}.json")));
                var sheet = GameDataJson.Read<BisSheet>(Path.Combine("data", "bis", job, "current.json"));
                var target = Rmc.Engine.GearsetInputs.FromBis(sheet.Meta, sheet.Sets[0]);
                return (Model: model, Target: target, Start: Rmc.Engine.Upgrades.StartingSets.Crafted(engine, model, target));
            }).ToList();
            var threads = Environment.ProcessorCount;
            foreach (var concurrent in new[] { 1, 2, 4, 8, threads }.Where(c => c <= threads).Distinct())
            {
                var pool = new Rmc.Engine.ComputePool(threads, concurrent);
                var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await Task.WhenAll(requests.Select(r => pool.RunAsync(degree =>
                {
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    var plan = new Rmc.Engine.Upgrades.UpgradePlanner(engine, r.Model).Plan(r.Start, r.Target, new Rmc.Engine.Upgrades.UpgradeOptions
                    {
                        WeeklyIncome = weekly.PerWeek, HuntCurrency = weekly.HuntCurrency, MaxDegreeOfParallelism = degree,
                    });
                    latencies.Add(w.Elapsed.TotalSeconds);
                    return plan.FinalDps;
                })));
                var total = watch.Elapsed.TotalSeconds;
                Console.WriteLine($"{concurrent,2} jobs x {pool.ThreadsPerJob,2} threads: {requests.Count} plans in {total:0.0}s "
                    + $"({requests.Count / total * 60:0} per minute), each {latencies.Average():0.0}s on average, slowest {latencies.Max():0.0}s");
            }
            return 0;
        }
        case "fit-profiles":
        {
            // Patch day, after refresh-potencies: refit every job's hit profile from its rotation sim.
            var data = new GameDataIndex(GameDataJson.ReadSnapshot(opts.GetValueOrDefault("gamedata",
                Directory.EnumerateFiles(Path.Combine("data", "gamedata"), "*.json").Order(StringComparer.Ordinal).Last())));
            var engine = new Rmc.Engine.StatEngine(data);
            // --seconds N fits another fight length (N-60 to N+60 in 30s steps, like the standard 510s) into data/profiles/<N>s.
            double[]? lengths = opts.TryGetValue("seconds", out var secondsText) && double.TryParse(secondsText, System.Globalization.CultureInfo.InvariantCulture, out var centre)
                ? [centre - 60, centre - 30, centre, centre + 30, centre + 60] : null;
            var outDir = opts.GetValueOrDefault("out", lengths != null ? Path.Combine("data", "profiles", $"{secondsText}s") : Path.Combine("data", "profiles"));
            var potencyDir = Rmc.Engine.Sim.Rotations.RotationRegistry.DefaultPotencyDirectory;
            var jobs = opts.TryGetValue("jobs", out var jobList)
                ? jobList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : Rmc.Engine.Sim.Rotations.RotationRegistry.Jobs.Order().ToArray();
            foreach (var job in jobs.Select(j => j.ToUpperInvariant()))
            {
                var sheet = GameDataJson.Read<BisSheet>(Path.Combine("data", "bis", job, "current.json"));
                var set = sheet.Sets[0];
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var profile = Rmc.Engine.Sim.ProfileFitter.Fit(engine, Rmc.Engine.GearsetInputs.FromBis(sheet.Meta, set), set.Name.Trim(), potencyDir,
                    fightSeconds: lengths, gameVersion: Rmc.Engine.Sim.PotencyTable.Load(potencyDir, job)?.GameVersion ?? "");
                GameDataJson.Write(Path.Combine(outDir, $"{job}.json"), profile);
                Console.WriteLine($"{job}: {profile.Tiers.Count} tiers ({profile.Tiers.First().GcdCs / 100.0:0.00}s to {profile.Tiers.Last().GcdCs / 100.0:0.00}s), "
                    + $"{profile.Tiers.Average(t => t.Hits.Count):0} hit groups per tier, {watch.Elapsed.TotalSeconds:0.0}s");
            }
            return 0;
        }
        case "status":
        {
            // Authoring aid: status (buff) descriptions by name, e.g. --find "Esprit,Flourishing Flow".
            var game = new Lumina.GameData(opts.GetValueOrDefault("game", DefaultSqpack), new Lumina.LuminaOptions());
            foreach (var name in Require(opts, "find").Split(',', StringSplitOptions.TrimEntries))
                foreach (var st in game.GetExcelSheet<Lumina.Excel.Sheets.Status>()!.Where(x => x.Name.ExtractText().Equals(name, StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"{st.RowId,6} {st.Name.ExtractText()}: {st.Description.ExtractText().Replace("\n", " ")}");
            return 0;
        }
        case "snapshot-xivgear":
        {
            var data = new GameDataIndex(GameDataJson.ReadSnapshot(Require(opts, "gamedata")));
            var outDir = opts.GetValueOrDefault("out", Path.Combine("data", "reference", "xivgear"));
            await new XivgearReferenceSnapshot(data, Console.Out).WriteAsync(outDir, opts.GetValueOrDefault("bis", Path.Combine("data", "bis")));
            return 0;
        }
        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{command} failed: {ex.Message}");
    return 3;
}

static string Require(Dictionary<string, string> opts, string key)
    => opts.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required.");

static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? key = null;
    foreach (var a in args)
    {
        if (a.StartsWith("--", StringComparison.Ordinal))
        {
            key = a[2..];
            result[key] = "true";
        }
        else if (key != null)
        {
            result[key] = a;
            key = null;
        }
    }
    return result;
}

static void PrintUsage()
{
    Console.WriteLine("""
        rmc-extract <command> [options]   (run from server/)

          extract           --patch 7.5 [--game <sqpack>] [--out data/gamedata/7.5.json]
                            [--level 100] [--min-ilvl 0] [--min-food-ilvl 600] [--overlays data/overlays]
              Read the local game files and write a game data snapshot.

          import-bis        --gamedata data/gamedata/7.5.json [--jobs DRG,WHM] [--sheet current] [--out data/bis]
                            [--url <xivgear link>[,<link>...]] [--refresh]
              Import The Balance sheets into our BiS format, validating every item, materia and food
              against the snapshot. Default source is the xivgear static-bis-sets repo; jobs that are only
              published as links (healers, BLM) take --url with one --jobs entry, merging the links' sets.
              --refresh re-imports every committed sheet from the source recorded in it.

          snapshot-xivgear  --gamedata data/gamedata/7.5.json [--bis data/bis] [--out data/reference/xivgear]
              Fetch xivgear's data API rows we check against (BaseParam, ItemLevel, Materia, BiS items).
              Dev-time only; the result is committed and the checks never go online.
        """);
}

internal sealed class WeeklyOverlay
{
    public string Tier { get; set; } = string.Empty;
    public Dictionary<string, int> PerWeek { get; set; } = new(StringComparer.Ordinal);
    public string HuntCurrency { get; set; } = "Sack of Nuts";
}
