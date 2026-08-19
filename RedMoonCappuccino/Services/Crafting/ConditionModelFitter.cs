using System;
using System.Collections.Generic;
using System.Linq;
using RedMoonCappuccino.Models;
using RedMoonCappuccino.Models.Crafting;

namespace RedMoonCappuccino.Services.Crafting;

/// <summary>One observed condition transition, already reduced to what the fit needs.</summary>
public readonly record struct ConditionTransition(ushort Flag, CraftCondition From, CraftCondition To);

/// <summary>
/// Fits a <see cref="ConditionModel"/> per <c>ConditionsFlag</c> from recorded transitions,
/// and grades each one against <see cref="ConditionModelGate"/>.
///
/// <para>The fit is of P(next | current), not of the marginal frequencies. That distinction
/// is what made the telegraph visible at all: a marginal fit looks perfect on the one
/// statistic it was fitted to while silently missing a deterministic rule governing a tenth
/// of all steps. The chi-square then <em>earns</em> the collapse back to an i.i.d. draw rather
/// than assuming it.</para>
/// </summary>
public static class ConditionModelFitter
{
    /// <summary>
    /// Reduce recorded samples to transitions, dropping the ones that would bias the fit.
    ///
    /// <para>Three exclusions, all deliberate.</para>
    ///
    /// <para><strong>Step gaps</strong> are dropped because a missing sample makes two
    /// non-adjacent conditions look adjacent.</para>
    ///
    /// <para><strong>Step-neutral samples</strong> are dropped because they come from
    /// specialist actions. Careful Observation draws from the ordinary distribution, so
    /// including them would not bias the <em>conditional</em>, but Study-mode runs spend
    /// rerolls on deliberately chosen conditions, and pooling measurement runs into a weight
    /// fit is the kind of contamination that has no symptom.</para>
    ///
    /// <para><strong>Samples with no recorded action</strong> are dropped, which is the
    /// subtle one. Careful Observation advances the condition without advancing the step
    /// counter, so in a session where actions were not captured an invisible specialist action
    /// forges a transition between two conditions that were never actually adjacent. Such a
    /// session cannot be certified free of them. The exclusion is on provenance — "we do not
    /// know what happened here" — and deliberately <em>not</em> on whether the transition
    /// violates the telegraph, since discarding transitions for contradicting the rule and
    /// then testing that rule would assume the conclusion. Measured effect on this corpus:
    /// every one of the five telegraph violations sits in an unlabelled session, and none
    /// survives the filter.</para>
    /// </summary>
    public static List<ConditionTransition> ToTransitions(
        IReadOnlyDictionary<string, CraftSessionHeader> sessions,
        IReadOnlyDictionary<string, List<CraftStepSample>> samplesBySession)
    {
        var transitions = new List<ConditionTransition>();

        foreach (var (sessionId, samples) in samplesBySession)
        {
            if (!sessions.TryGetValue(sessionId, out var header)) continue;

            for (var i = 0; i + 1 < samples.Count; i++)
            {
                var a = samples[i];
                var b = samples[i + 1];

                // Absent trigger is the legacy recording, which only ever emitted on a step change.
                var trigger = string.IsNullOrEmpty(b.Trigger) ? "step" : b.Trigger;
                if (trigger != "step") continue;

                if (b.Step != a.Step + 1) continue;

                // No recorded action means the session's actions were never captured, so a
                // step-neutral specialist action in it is invisible and would forge this pair.
                if (a.ActionId == 0) continue;

                var from = ConditionEffects.FromDisplayName(a.Condition);
                var to   = ConditionEffects.FromDisplayName(b.Condition);
                if (from == CraftCondition.Unknown || to == CraftCondition.Unknown) continue;

                transitions.Add(new ConditionTransition(header.ConditionsFlag, from, to));
            }
        }

        return transitions;
    }

    /// <summary>
    /// Fit one flag. Returns a graded model — including for a flag with no data at all, which
    /// comes back <see cref="ConditionModelStatus.Absent"/> rather than null, so that callers
    /// cannot accidentally treat "no model" as "no problem".
    /// </summary>
    public static ConditionModel Fit(ushort flag, IReadOnlyList<ConditionTransition> transitions)
    {
        var members = ConditionEffects.Decode(flag);
        var declared = ConditionEffects.DeclaredConditionCount(flag);

        // Whichever telegraph this flag carries, if either. No flag carries both.
        var telegraphSource = members.FirstOrDefault(
            m => ConditionEffects.Telegraphs(m) != CraftCondition.Unknown,
            CraftCondition.Unknown);
        var telegraphTarget = ConditionEffects.Telegraphs(telegraphSource);

        var relevant = transitions.Where(t => t.Flag == flag).ToArray();

        // ── Telegraph ──
        var telegraphRows = relevant.Where(t => t.From == telegraphSource).ToArray();
        var telegraphHonoured = telegraphRows.Count(t => t.To == telegraphTarget);

        // ── The i.i.d. half ──
        var fitted = telegraphSource == CraftCondition.Unknown
            ? relevant
            : relevant.Where(t => t.From != telegraphSource).ToArray();

        var counts = new int[ConditionEffects.TableSize];
        foreach (var t in fitted) counts[(int)t.To]++;

        var total = fitted.Length;
        var weights = new double[ConditionEffects.TableSize];
        var maxHalfWidth = 0.0;

        if (total > 0)
        {
            foreach (var member in members)
            {
                var p = counts[(int)member] / (double)total;
                weights[(int)member] = p;

                // 95% half-interval on a binomial proportion.
                var halfWidth = 1.96 * Math.Sqrt(p * (1 - p) / total);
                maxHalfWidth = Math.Max(maxHalfWidth, halfWidth);
            }
        }

        // ── Coverage ──
        // Conditions the flag declares but the sample never produced. Their weights would be
        // exactly zero, which is a wrong value rather than an imprecise one.
        var observedTargets = new HashSet<CraftCondition>(relevant.Select(t => t.To));
        foreach (var t in relevant) observedTargets.Add(t.From);

        var unobserved = members.Where(m => !observedTargets.Contains(m)).ToArray();

        var chi = ChiSquareHomogeneity(fitted, members);

        var evidence = new ConditionModelEvidence
        {
            FittedTransitions    = total,
            TelegraphTransitions = telegraphRows.Length,
            TelegraphHonoured    = telegraphHonoured,
            ChiSquare            = chi.Statistic,
            DegreesOfFreedom     = chi.DegreesOfFreedom,
            PValue               = chi.PValue,
            MaxHalfWidth         = maxHalfWidth,
            DistinctObserved     = observedTargets.Count,
            DeclaredCount        = declared,
            UnobservedConditions = unobserved,
        };

        return new ConditionModel
        {
            Flag            = flag,
            Members         = members,
            TelegraphSource = telegraphSource,
            TelegraphTarget = telegraphTarget,
            Weights         = weights,
            Evidence        = evidence,
            Status          = ConditionModelGate.Grade(evidence),
        };
    }

    // ── Statistics ────────────────────────────────────────────────────────────

    private readonly record struct ChiSquareResult(double Statistic, int DegreesOfFreedom, double PValue);

    /// <summary>
    /// Test whether every non-telegraph source draws from the same distribution.
    ///
    /// This is the test the model has to pass to be allowed to collapse a full transition
    /// matrix into a single seven-parameter draw. Passing it is what turns "the rows look
    /// similar" into "the rows are one row".
    /// </summary>
    private static ChiSquareResult ChiSquareHomogeneity(
        IReadOnlyList<ConditionTransition> transitions,
        IReadOnlyList<CraftCondition> members)
    {
        var sources = members.Where(m => transitions.Any(t => t.From == m)).ToArray();
        var targets = members.Where(m => transitions.Any(t => t.To == m)).ToArray();

        if (sources.Length < 2 || targets.Length < 2)
            return new ChiSquareResult(0, 0, 1.0);

        var observed = new int[sources.Length, targets.Length];
        foreach (var t in transitions)
        {
            var r = Array.IndexOf(sources, t.From);
            var c = Array.IndexOf(targets, t.To);
            if (r >= 0 && c >= 0) observed[r, c]++;
        }

        var rowTotals = new int[sources.Length];
        var colTotals = new int[targets.Length];
        var grand = 0;

        for (var r = 0; r < sources.Length; r++)
        for (var c = 0; c < targets.Length; c++)
        {
            rowTotals[r] += observed[r, c];
            colTotals[c] += observed[r, c];
            grand        += observed[r, c];
        }

        if (grand == 0) return new ChiSquareResult(0, 0, 1.0);

        var statistic = 0.0;
        for (var r = 0; r < sources.Length; r++)
        for (var c = 0; c < targets.Length; c++)
        {
            var expected = rowTotals[r] * (double)colTotals[c] / grand;
            if (expected <= 0) continue;
            var diff = observed[r, c] - expected;
            statistic += diff * diff / expected;
        }

        var df = (sources.Length - 1) * (targets.Length - 1);
        return new ChiSquareResult(statistic, df, ChiSquareUpperTail(statistic, df));
    }

    /// <summary>
    /// P(chi² &gt;= <paramref name="x"/>) with <paramref name="df"/> degrees of freedom — the
    /// regularised upper incomplete gamma Q(df/2, x/2), by series below the transition point
    /// and continued fraction above it.
    /// </summary>
    public static double ChiSquareUpperTail(double x, int df)
    {
        if (df <= 0) return 1.0;
        if (x <= 0) return 1.0;
        return GammaQ(df / 2.0, x / 2.0);
    }

    private static double GammaQ(double a, double x)
    {
        if (x < a + 1.0) return 1.0 - GammaSeries(a, x);
        return GammaContinuedFraction(a, x);
    }

    private static double GammaSeries(double a, double x)
    {
        const int MaxIterations = 500;
        const double Epsilon = 1e-14;

        var ap = a;
        var sum = 1.0 / a;
        var term = sum;

        for (var i = 0; i < MaxIterations; i++)
        {
            ap += 1.0;
            term *= x / ap;
            sum += term;
            if (Math.Abs(term) < Math.Abs(sum) * Epsilon) break;
        }

        return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
    }

    private static double GammaContinuedFraction(double a, double x)
    {
        const int MaxIterations = 500;
        const double Epsilon = 1e-14;
        const double Tiny = 1e-300;

        var b = x + 1.0 - a;
        var c = 1.0 / Tiny;
        var d = 1.0 / b;
        var h = d;

        for (var i = 1; i <= MaxIterations; i++)
        {
            var an = -i * (i - a);
            b += 2.0;

            d = an * d + b;
            if (Math.Abs(d) < Tiny) d = Tiny;

            c = b + an / c;
            if (Math.Abs(c) < Tiny) c = Tiny;

            d = 1.0 / d;
            var delta = d * c;
            h *= delta;

            if (Math.Abs(delta - 1.0) < Epsilon) break;
        }

        return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
    }

    /// <summary>Lanczos approximation; accurate well beyond what a p-value threshold needs.</summary>
    private static double LogGamma(double x)
    {
        double[] coefficients =
        {
            76.18009172947146, -86.50532032941677, 24.01409824083091,
            -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5,
        };

        var y = x;
        var tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);

        var series = 1.000000000190015;
        foreach (var c in coefficients)
        {
            y += 1.0;
            series += c / y;
        }

        return -tmp + Math.Log(2.5066282746310005 * series / x);
    }
}
