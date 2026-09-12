using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using RedMoonCappuccino.Models;

namespace RedMoonCappuccino.Services;

/// <summary>
/// Reads the player's Series Malmstone progress and PvP currencies straight from
/// the client, so the calculator needs nothing typed in.
///
/// The PvP profile lives in client memory and is populated by the server at login
/// and after each match; reading it is a plain memory read with no request behind
/// it. Even so, <see cref="Capture"/> is only called when the main window opens or
/// on an explicit refresh — never per frame — and must run on the framework thread.
/// </summary>
public sealed unsafe class PvpSeriesService
{
    private const uint WolfMarkItemId      = 25;
    private const uint TrophyCrystalItemId = 36656;

    /// <summary>Icons for the two currencies, for the UI to draw beside the counts.</summary>
    public const uint WolfMarkIconId      = 65014;
    public const uint TrophyCrystalIconId = 65090;

    /// <summary>Generic PvP content icon, used for the tab header.</summary>
    public const uint PvpIconId = 61806;

    private readonly IClientState clientState;
    private readonly IPluginLog   log;

    public PvpSeriesCalculator Calculator { get; }

    /// <summary>True when the level curve came from the game sheet rather than the built-in table.</summary>
    public bool LevelCurveFromGameData { get; }

    public PvpSeriesService(IDataManager dataManager, IClientState clientState, IPluginLog log)
    {
        this.clientState = clientState;
        this.log         = log;

        var curve = LoadLevelCurve(dataManager, log);
        LevelCurveFromGameData = curve != null;
        Calculator = new PvpSeriesCalculator(curve ?? PvpSeriesCalculator.FallbackExpToNext);
    }

    /// <summary>Reads the profile now. Framework thread only.</summary>
    public PvpSeriesSnapshot Capture()
    {
        try
        {
            if (!clientState.IsLoggedIn)
                return PvpSeriesSnapshot.Unavailable(PvpSeriesStatus.NotLoggedIn);

            var profile = PvPProfile.Instance();
            if (profile == null || !profile->IsLoaded)
                return PvpSeriesSnapshot.Unavailable(PvpSeriesStatus.ProfileNotLoaded);

            return new PvpSeriesSnapshot
            {
                Status         = PvpSeriesStatus.Ok,
                Series         = profile->Series,
                Rank           = profile->SeriesCurrentRank,
                ClaimedRank    = profile->SeriesClaimedRank,
                Experience     = profile->SeriesExperience,
                WolfMarks      = CountCurrency(WolfMarkItemId),
                TrophyCrystals = CountCurrency(TrophyCrystalItemId),
                CapturedAtUtc  = DateTime.UtcNow,
            };
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[PvpSeries] Failed to read the PvP profile.");
            return PvpSeriesSnapshot.Unavailable(PvpSeriesStatus.Failed);
        }
    }

    /// <summary>
    /// Asks the game for the count rather than scanning the Currency container: Wolf
    /// Marks sit in that container but Trophy Crystals do not, and the game's own lookup
    /// knows where each currency lives (CurrencyAlert and Umbra count the same way).
    /// Equipped and armoury are skipped — a currency is never in either.
    /// </summary>
    private static int CountCurrency(uint itemId)
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;
        return manager->GetInventoryItemCount(itemId, isHq: false, checkEquipped: false, checkArmory: false);
    }

    /// <summary>
    /// EXP-to-next per level from the <c>PvPSeriesLevel</c> sheet, indexed by level,
    /// or null when the sheet is missing or looks empty.
    /// </summary>
    private static IReadOnlyList<int>? LoadLevelCurve(IDataManager dataManager, IPluginLog log)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.PvPSeriesLevel>();
            if (sheet == null) return null;

            var byLevel = new SortedDictionary<uint, int>();
            foreach (var row in sheet)
                byLevel[row.RowId] = row.ExpToNext;
            if (byLevel.Count < 2) return null;

            uint maxLevel = 0;
            foreach (var level in byLevel.Keys) maxLevel = Math.Max(maxLevel, level);

            var curve = new int[maxLevel + 1];
            foreach (var (level, exp) in byLevel)
                curve[level] = exp;

            // A curve with no positive step is a broken read, not a real table.
            var anyPositive = false;
            for (var l = 1; l < curve.Length; l++)
                if (curve[l] > 0) { anyPositive = true; break; }
            return anyPositive ? curve : null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[PvpSeries] Could not read PvPSeriesLevel; using the built-in level curve.");
            return null;
        }
    }
}
