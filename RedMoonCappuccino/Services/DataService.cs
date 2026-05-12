using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        List<string> toFetch;
        lock (stateLock)
        {
            tax = snapshot.Tax;
            events = snapshot.Events ?? new();
            imageManifests = snapshot.Images ?? new();
            lastUpdated = snapshot.UpdatedAt;

            toFetch = imageManifests
                .Where(m => !cachedImages.ContainsKey(m.EventId)
                         && !pendingImageRequests.ContainsKey(m.EventId))
                .Select(m => m.EventId)
                .ToList();
        }

        foreach (var eventId in toFetch)
        {
            pendingImageRequests[eventId] = true;
            OnImageNeeded?.Invoke(eventId);
        }
    }

    public void UpdateImage(ImageResponseMessage response)
    {
        pendingImageRequests.TryRemove(response.EventId, out _);

        if (response.Image == null || string.IsNullOrEmpty(response.Image.Data))
            return;

        try
        {
            var bytes = Convert.FromBase64String(response.Image.Data);
            var ext = response.Image.MimeType == "image/png" ? "png" : "jpg";
            var filePath = Path.Combine(imageCacheDir, $"{response.EventId}.{ext}");
            File.WriteAllBytes(filePath, bytes);
            cachedImages[response.EventId] = filePath;
            log.Information($"[RedMoonCappuccino] Cached image for event {response.EventId}");
        }
        catch (Exception ex)
        {
            log.Warning($"[RedMoonCappuccino] Failed to save image for {response.EventId}: {ex.Message}");
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

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
