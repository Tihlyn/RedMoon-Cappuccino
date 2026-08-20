using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using RedMoonCappuccino.Models;

namespace RedMoonCappuccino.Services;

/// <summary>
/// Watches the free company of the logged-in character and keeps a hashed
/// snapshot of its member names, so the websocket layer can report the roster
/// to the server without shipping one on every ping.
///
/// Only names are read. Content ids are never touched — the server treats the
/// sorted name list as the roster and handles renames on its own side.
///
/// Everything here runs on the framework thread. The result is published as an
/// immutable <see cref="FcRosterSnapshot"/> in <see cref="Current"/>, which the
/// websocket threads read without ever re-entering game memory.
/// </summary>
public sealed class FreeCompanyRosterService : IDisposable
{
    /// <summary>Version tag baked into the hash so the recipe can change without a silent mismatch.</summary>
    public const string HashVersionTag = "rmc-fc-v1";

    private const long PollIntervalMs = 5000;

    /// <summary>A free company cannot exceed 512 members; anything larger is a misread.</summary>
    private const int MaxMembers = 512;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly Configuration configuration;
    private readonly IPluginLog log;

    private volatile FcRosterSnapshot? current;
    private long lastPollTick;
    private long lastRequestTick;
    private ulong lastSeenFcId;

    public FreeCompanyRosterService(IFramework framework, IClientState clientState, IObjectTable objectTable,
                                    IGameGui gameGui, IDataManager dataManager,
                                    Configuration configuration, IPluginLog log)
    {
        this.framework     = framework;
        this.clientState   = clientState;
        this.objectTable   = objectTable;
        this.gameGui       = gameGui;
        this.dataManager   = dataManager;
        this.configuration = configuration;
        this.log           = log;

        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;

    /// <summary>
    /// The last complete roster read, or null when the character is logged out,
    /// is not in the watched free company, or the member list has never loaded.
    /// Safe to read from any thread.
    /// </summary>
    public FcRosterSnapshot? Current => current;

    // ── Framework polling ─────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = Environment.TickCount64;
        if (now - lastPollTick < PollIntervalMs) return;
        lastPollTick = now;

        if (!configuration.FcRosterSync || !clientState.IsLoggedIn)
        {
            Clear();
            return;
        }

        try
        {
            Poll(now);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[RedMoonCappuccino] Free company roster poll failed.");
        }
    }

    private unsafe void Poll(long now)
    {
        var fc = InfoProxyFreeCompany.Instance();
        if (fc == null || fc->Id == 0)
        {
            Clear();
            return;
        }

        var fcName = fc->NameString?.Trim() ?? string.Empty;
        if (!MatchesWatchedFc(fcName))
        {
            Clear();
            return;
        }

        // Switching to a character in a different free company invalidates
        // whatever was cached, even when the name still matches.
        if (fc->Id != lastSeenFcId)
        {
            lastSeenFcId    = fc->Id;
            lastRequestTick = 0;
            current         = null;
        }

        // Self-throttled; asks the game to (re)load the member list so joins and
        // leaves are seen without the player opening the free company window.
        MaybeRequestRoster(now);

        var total = fc->TotalMembers;
        if (total == 0) return;

        var proxy = InfoProxyFreeCompanyMember.Instance();
        if (proxy == null) return;

        // The list arrives in pages. Anything short of the full count is a load
        // in progress — hashing it would report a roster that never existed, so
        // the previously published snapshot is kept until the load completes.
        var count = proxy->GetEntryCount();
        if (count == 0 || count != total) return;
        if (proxy->FreeCompanyId != 0 && proxy->FreeCompanyId != fc->Id) return;
        if (count > MaxMembers) return;

        var names = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            var entry = proxy->GetEntry(i);
            if (entry == null) return;

            var name = entry->NameString?.Trim() ?? string.Empty;
            if (name.Length == 0) return;

            names.Add(name);
        }

        // Proxy order is whatever column the player last sorted by, so the list
        // is ordered here. A duplicate means a page was caught mid-write.
        var ordered = names.Distinct(StringComparer.Ordinal)
                           .OrderBy(n => n, StringComparer.Ordinal)
                           .ToArray();
        if (ordered.Length != total) return;

        var hash = ComputeHash(ordered);

        var existing = current;
        if (existing != null && existing.Hash == hash) return;

        current = new FcRosterSnapshot
        {
            FcId       = fc->Id.ToString("x"),
            FcName     = fcName,
            WorldId    = fc->HomeWorldId,
            World      = ResolveWorldName(fc->HomeWorldId),
            Hash       = hash,
            Members    = ordered,
            Reporter   = (objectTable.LocalPlayer as ICharacter)?.Name.ToString().Trim() ?? string.Empty,
            CapturedAt = DateTime.UtcNow,
        };

        log.Information($"[RedMoonCappuccino] FC roster snapshot: {ordered.Length} members, hash {hash}.");
    }

    /// <summary>
    /// Asks the game to reload the free company member list, at most once per
    /// configured interval. The proxy is shared with the free company window, so
    /// nothing is requested while that window is open.
    /// </summary>
    private unsafe void MaybeRequestRoster(long now)
    {
        if (!configuration.FcRosterActiveRefresh) return;

        var cooldownMs = Math.Max(5, configuration.FcRosterRefreshMinutes) * 60_000L;
        if (lastRequestTick != 0 && now - lastRequestTick < cooldownMs) return;

        if (gameGui.GetAddonByName("FreeCompany") != nint.Zero) return;

        var proxy = InfoProxyFreeCompanyMember.Instance();
        if (proxy == null) return;

        lastRequestTick = now;

        if (!proxy->RequestData())
            log.Debug("[RedMoonCappuccino] Free company member list request was refused by the game.");
    }

    private void Clear()
    {
        current         = null;
        lastSeenFcId    = 0;
        lastRequestTick = 0;
    }

    private bool MatchesWatchedFc(string fcName)
    {
        var watched = configuration.FcRosterName;
        if (string.IsNullOrWhiteSpace(watched) || fcName.Length == 0) return false;
        return string.Equals(fcName, watched.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveWorldName(ushort worldId)
    {
        try
        {
            return dataManager.GetExcelSheet<World>()?.GetRowOrDefault(worldId)?.Name.ExtractText() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── Hashing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// SHA-256 over the version tag followed by the ordinal-sorted member names,
    /// one per line, UTF-8, no trailing newline; first 16 bytes as lowercase hex.
    /// The server recomputes this from the uploaded list to verify a snapshot.
    /// </summary>
    public static string ComputeHash(IReadOnlyList<string> orderedNames)
    {
        var sb = new StringBuilder(HashVersionTag);
        foreach (var name in orderedNames)
            sb.Append('\n').Append(name);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }
}
