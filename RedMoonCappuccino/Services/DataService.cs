using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RedMoonCappuccino.Models;

namespace RedMoonCappuccino.Services;

public class DataService : IDisposable
{
    private readonly IPluginLog log;
    private readonly string imageCacheDir;

    private readonly object stateLock = new();
    private TaxInfo? tax;
    private List<EventSummary> events = new();
    private List<ImageManifest> imageManifests = new();
    private DateTime lastUpdated;

    // eventId -> local file path (thread-safe dict)
    private readonly ConcurrentDictionary<string, string> cachedImages = new();

    // eventId -> true while we're waiting for an image response
    private readonly ConcurrentDictionary<string, bool> pendingImageRequests = new();

    /// <summary>
    /// Called when a new image needs to be fetched from the server.
    /// Assigned from Plugin after WebSocketService is created.
    /// </summary>
    public Action<string>? OnImageNeeded { get; set; }

    public DataService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        imageCacheDir = Path.Combine(pluginInterface.ConfigDirectory.FullName, "images");
        Directory.CreateDirectory(imageCacheDir);
        LoadExistingCachedImages();
        CleanupOldImages();
    }

    // ── Public thread-safe accessors ────────────────────────────────────────

    public TaxInfo? Tax
    {
        get { lock (stateLock) return tax; }
    }

    public DateTime LastUpdated
    {
        get { lock (stateLock) return lastUpdated; }
    }

    public List<EventSummary> GetUpcomingEvents()
    {
        var now = DateTime.UtcNow;
        lock (stateLock)
            return events.Where(e => e.Date >= now).OrderBy(e => e.Date).ToList();
    }

    public List<EventSummary> GetPastEvents()
    {
        var now = DateTime.UtcNow;
        var cutoff = now - TimeSpan.FromHours(24);
        lock (stateLock)
            return events.Where(e => e.Date < now && e.Date >= cutoff)
                         .OrderByDescending(e => e.Date)
                         .ToList();
    }

    public bool HasImageManifest(string eventId)
    {
        lock (stateLock)
            return imageManifests.Any(m => m.EventId == eventId);
    }

    public string? GetCachedImagePath(string eventId)
        => cachedImages.TryGetValue(eventId, out var path) ? path : null;

    public bool IsImagePending(string eventId)
        => pendingImageRequests.ContainsKey(eventId);

    // ── Mutation methods (called from WebSocket thread) ──────────────────────

    public void UpdateSnapshot(SnapshotMessage snapshot)
    {
        var manifests = snapshot.Images ?? new();
        // Collect entries that have a cached image so we can check staleness outside the lock.
        var toCheck = new List<(string eventId, string cachedPath, DateTime updatedAt)>();

        lock (stateLock)
        {
            tax = snapshot.Tax;
            events = snapshot.Events ?? new();
            imageManifests = manifests;
            lastUpdated = snapshot.UpdatedAt;

            foreach (var manifest in manifests)
            {
                if (cachedImages.TryGetValue(manifest.EventId, out var cachedPath))
                    toCheck.Add((manifest.EventId, cachedPath, manifest.UpdatedAt));
            }
        }

        // Perform file I/O and re-fetch outside the lock to keep the critical section short.
        foreach (var (eventId, cachedPath, updatedAt) in toCheck)
        {
            try
            {
                var info = new FileInfo(cachedPath);
                if (!info.Exists || info.LastWriteTimeUtc < updatedAt)
                {
                    if (info.Exists)
                        File.Delete(cachedPath);
                    cachedImages.TryRemove(eventId, out _);
                    RequestImageIfNeeded(eventId);
                }
            }
            catch (Exception ex)
            {
                log.Warning($"[RedMoonCappuccino] Could not check cached image for {eventId}: {ex.Message}");
                cachedImages.TryRemove(eventId, out _);
                RequestImageIfNeeded(eventId);
            }
        }
    }

    /// <summary>
    /// Called by the UI when an event row is expanded and an image is needed
    /// but has not yet been fetched or cached. Safe to call every frame —
    /// it no-ops if a request is already in flight or the image is on disk.
    /// </summary>
    public void RequestImageIfNeeded(string eventId)
    {
        if (cachedImages.ContainsKey(eventId) || pendingImageRequests.ContainsKey(eventId))
            return;

        if (pendingImageRequests.TryAdd(eventId, true))
            OnImageNeeded?.Invoke(eventId);
    }

    public void UpdateImage(ImageResponseMessage response)
    {
        pendingImageRequests.TryRemove(response.EventId, out _);

        if (response.Image == null || string.IsNullOrEmpty(response.Image.Data))
            return;

        try
        {
            var bytes = Convert.FromBase64String(response.Image.Data);
            var safeFileName = SanitizeEventId(response.EventId) + ".png";
            var filePath = Path.Combine(imageCacheDir, safeFileName);

            // Guard against path traversal: ensure the resolved path stays inside the cache dir.
            var resolvedPath = Path.GetFullPath(filePath);
            if (!resolvedPath.StartsWith(Path.GetFullPath(imageCacheDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                log.Warning($"[RedMoonCappuccino] Rejected unsafe image path for event {response.EventId}");
                return;
            }

            File.WriteAllBytes(resolvedPath, bytes);
            cachedImages[response.EventId] = resolvedPath;
            log.Information($"[RedMoonCappuccino] Cached image for event {response.EventId}");
        }
        catch (Exception ex)
        {
            log.Warning($"[RedMoonCappuccino] Failed to save image for {response.EventId}: {ex.Message}");
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns a filesystem-safe version of an event ID by keeping only
    /// alphanumeric characters, hyphens, and underscores.
    /// </summary>
    private static string SanitizeEventId(string eventId)
        => Regex.Replace(eventId, @"[^A-Za-z0-9_\-]", "_");

    private void LoadExistingCachedImages()
    {
        foreach (var file in Directory.GetFiles(imageCacheDir))
        {
            var eventId = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrEmpty(eventId))
                cachedImages[eventId] = file;
        }
    }

    private void CleanupOldImages()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        foreach (var file in Directory.GetFiles(imageCacheDir))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    File.Delete(file);
                    var eventId = Path.GetFileNameWithoutExtension(file);
                    if (!string.IsNullOrEmpty(eventId))
                        cachedImages.TryRemove(eventId, out _);
                    log.Debug($"[RedMoonCappuccino] Deleted stale cached image: {file}");
                }
            }
            catch (Exception ex)
            {
                log.Warning($"[RedMoonCappuccino] Could not clean up image {file}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        // Run cleanup on a background thread to avoid blocking the main thread during unload.
        _ = System.Threading.Tasks.Task.Run(CleanupOldImages);
    }
}
