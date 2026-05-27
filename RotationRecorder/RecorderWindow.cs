using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace RedMoonCappuccino.RotationRecorder;

/// <summary>
/// Main recorder window.
///
/// Constructor dependencies — inject from Plugin.cs:
///   Plugin          plugin         (for Config and services)
///   ActionRecorder  recorder       (the hook-based session recorder)
///   GeminiAnalyzer  analyzer       (the API client)
/// </summary>
public sealed class RecorderWindow : Window, IDisposable
{
    // ── Injected ──────────────────────────────────────────────────────────────
    private readonly Plugin        _plugin;
    private readonly ActionRecorder _recorder;
    private readonly GeminiAnalyzer _analyzer;

    // ── Analysis task state ───────────────────────────────────────────────────
    private CancellationTokenSource? _analyzeCts;
    private Task<string>?            _analyzeTask;
    private string                   _analyzeResult = "";
    private string                   _analyzeError  = "";
    private bool                     Analyzing      => _analyzeTask is { IsCompleted: false };

    // ── Layout constants ──────────────────────────────────────────────────────
    private const float TimelineHeight  = 220f;
    private const float ResultsHeight   = 280f;

    public RecorderWindow(Plugin plugin, ActionRecorder recorder, GeminiAnalyzer analyzer)
        : base("Rotation Recorder##RecorderWindow",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _plugin   = plugin;
        _recorder = recorder;
        _analyzer = analyzer;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 480),
            MaximumSize = new Vector2(900, 900),
        };
        Size          = new Vector2(560, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
        _analyzeCts?.Cancel();
        _analyzeCts?.Dispose();
    }

    // ── Draw ──────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        PollAnalysisTask();

        DrawRecordSection();
        ImGui.Separator();
        DrawTimeline();
        ImGui.Separator();
        DrawAnalyseSection();
    }

    // ── Record section ────────────────────────────────────────────────────────

    private void DrawRecordSection()
    {
        var isRecording = _recorder.IsRecording;
        var events      = _recorder.Events.ToArray();   // snapshot for this frame

        // Record / Stop button
        if (isRecording)
        {
            using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.7f, 0.15f, 0.15f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.2f, 0.2f, 1f)))
            {
                if (ImGui.Button("  ⏹  Stop Recording  "))
                    _recorder.Stop();
            }

            ImGui.SameLine();
            var elapsed = (DateTimeOffset.UtcNow - (_recorder.SessionStart ?? DateTimeOffset.UtcNow)).TotalSeconds;
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.9f, 0.4f, 0.4f, 1f)))
                ImGui.TextUnformatted($"● REC  {elapsed:F0}s  |  {events.Length} actions");
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Button,        new Vector4(0.15f, 0.55f, 0.15f, 1f)))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.2f, 0.7f, 0.2f, 1f)))
            {
                if (ImGui.Button("  ▶  Start Recording  "))
                {
                    _recorder.Start();
                    _analyzeResult = "";
                    _analyzeError  = "";
                }
            }

            if (events.Length > 0)
            {
                ImGui.SameLine();
                if (ImGui.Button("Clear"))
                {
                    _recorder.Clear();
                    _analyzeResult = "";
                    _analyzeError  = "";
                }

                var dur = _recorder.SessionEnd.HasValue && _recorder.SessionStart.HasValue
                    ? (_recorder.SessionEnd.Value - _recorder.SessionStart.Value).TotalSeconds
                    : 0.0;

                ImGui.SameLine();
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f)))
                    ImGui.TextUnformatted($"{events.Length} actions recorded ({dur:F1}s)");
            }
        }
    }

    // ── Timeline section ──────────────────────────────────────────────────────

    private void DrawTimeline()
    {
        var events = _recorder.Events.ToArray();

        ImGui.TextUnformatted("Timeline");
        ImGui.Spacing();

        var childSize = new Vector2(0, TimelineHeight * ImGuiHelpers.GlobalScale);
        using var child = ImRaii.Child("##timeline", childSize, border: true);
        if (!child) return;

        if (events.Length == 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f)))
                ImGui.TextUnformatted("No actions recorded yet. Press Start, then hit a training dummy.");
            return;
        }

        var origin   = events[0].Timestamp;
        var lastGcd  = default(ActionEvent?);

        foreach (var ev in events)
        {
            var t    = (ev.Timestamp - origin).TotalSeconds;
            var kind = ev.IsGcd ? " GCD" : "oGCD";

            // Color: GCDs = white, oGCDs = light blue, large gaps = yellow
            var col = new Vector4(1f, 1f, 1f, 1f);

            if (!ev.IsGcd)
                col = new Vector4(0.4f, 0.8f, 1f, 1f);

            double gap = 0;
            if (ev.IsGcd && lastGcd != null)
            {
                gap = (ev.Timestamp - lastGcd.Timestamp).TotalSeconds;
                if (gap > 3.2)
                    col = new Vector4(1f, 0.8f, 0.2f, 1f);   // yellow = gap warning
            }
            if (ev.IsGcd) lastGcd = ev;

            using (ImRaii.PushColor(ImGuiCol.Text, col))
            {
                var gapStr = (ev.IsGcd && gap > 3.2) ? $"  ⚠ {gap:F1}s gap" : "";
                var dmgStr = ev.DamageDealt.HasValue  ? $"  [{ev.DamageDealt}]" : "";
                ImGui.TextUnformatted(
                    $"  {t,6:F2}s  [{kind}]  {ev.ActionName,-30}{dmgStr}{gapStr}");
            }
        }
    }

    // ── Analyse section ───────────────────────────────────────────────────────

    private void DrawAnalyseSection()
    {
        DrawQuotaBar();
        ImGui.Spacing();
        DrawAnalyseButton();
        ImGui.Spacing();
        DrawAnalysisResults();
    }

    private void DrawQuotaBar()
    {
        var remaining = _plugin.Config.GroundedRemaining;
        var used      = Configuration.GroundedHardLimit - remaining;
        var fraction  = (float)used / Configuration.GroundedHardLimit;

        // Bar colour: green → yellow → red
        var barCol = fraction switch
        {
            < 0.70f => new Vector4(0.25f, 0.75f, 0.25f, 1f),
            < 0.90f => new Vector4(0.90f, 0.70f, 0.10f, 1f),
            _       => new Vector4(0.90f, 0.20f, 0.20f, 1f),
        };

        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, barCol))
            ImGui.ProgressBar(fraction, new Vector2(-1f, 5f * ImGuiHelpers.GlobalScale), "");

        ImGui.Spacing();

        if (remaining <= 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.9f, 0.2f, 0.2f, 1f)))
                ImGui.TextUnformatted(
                    $"Daily limit reached ({Configuration.GroundedHardLimit} queries used). " +
                    "Resets at midnight UTC.");
        }
        else if (remaining <= Configuration.GroundedSoftWarnAt)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.9f, 0.7f, 0.1f, 1f)))
                ImGui.TextUnformatted($"⚠ {remaining} grounded queries remaining today");
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f)))
                ImGui.TextUnformatted(
                    $"Grounded queries today: {used} / {Configuration.GroundedHardLimit}   " +
                    $"({remaining} remaining, resets midnight UTC)");
        }
    }

    private void DrawAnalyseButton()
    {
        var events     = _recorder.Events.ToArray();
        var hasEvents  = events.Length > 0;
        var hasKey     = !string.IsNullOrWhiteSpace(_plugin.Config.GeminiApiKey);
        var hasQuota   = _plugin.Config.CanGroundedQuery;
        var canAnalyze = hasEvents && hasKey && hasQuota && !Analyzing && !_recorder.IsRecording;

        using (ImRaii.Disabled(!canAnalyze))
        {
            if (ImGui.Button("  Analyse Rotation  ") && canAnalyze)
                StartAnalysis(events);
        }

        // Status text next to button
        if (!hasKey)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.9f, 0.3f, 0.3f, 1f)))
                ImGui.TextUnformatted("⚠ No API key — set one in Settings");
        }
        else if (_recorder.IsRecording)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f)))
                ImGui.TextUnformatted("Stop recording first");
        }
        else if (!hasQuota)
        {
            // Already shown in quota bar — no duplicate needed
        }
        else if (Analyzing)
        {
            ImGui.SameLine();
            var spinner = "|/-\\"[(int)(ImGui.GetTime() * 4) % 4];
            ImGui.TextUnformatted($"Analysing… {spinner}");
        }
        else if (!hasEvents)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f)))
                ImGui.TextUnformatted("Record a session first");
        }
    }

    private void DrawAnalysisResults()
    {
        if (!string.IsNullOrEmpty(_analyzeError))
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.9f, 0.3f, 0.3f, 1f)))
                ImGui.TextUnformatted($"Error: {_analyzeError}");
        }

        if (string.IsNullOrEmpty(_analyzeResult)) return;

        ImGui.Spacing();
        ImGui.TextUnformatted("── Analysis ────────────────────────────────────────");
        ImGui.Spacing();

        var childSize = new Vector2(0, ResultsHeight * ImGuiHelpers.GlobalScale);
        using (var child = ImRaii.Child("##analysis", childSize, border: true))
        {
            if (child)
            {
                ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
                ImGui.TextUnformatted(_analyzeResult);
                ImGui.PopTextWrapPos();
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Copy"))
            ImGui.SetClipboardText(_analyzeResult);
        ImGui.SameLine();
        if (ImGui.Button("Clear##result"))
        {
            _analyzeResult = "";
            _analyzeError  = "";
        }
    }

    // ── Analysis task management ──────────────────────────────────────────────

    private void StartAnalysis(ActionEvent[] events)
    {
        _analyzeCts?.Cancel();
        _analyzeCts    = new CancellationTokenSource();
        _analyzeResult = "";
        _analyzeError  = "";

        var player  = Plugin.ObjectTable.LocalPlayer;
        var job     = player?.ClassJob.Value.Abbreviation.ExtractText() ?? "Unknown";
        var start   = _recorder.SessionStart ?? events.First().Timestamp;
        var end     = _recorder.SessionEnd   ?? events.Last().Timestamp;

        // Guard: check quota before firing (belt-and-suspenders over the disabled button)
        if (!_plugin.Config.CanGroundedQuery)
        {
            _analyzeError = $"Daily limit reached ({Configuration.GroundedHardLimit} queries). " +
                            "Resets at midnight UTC.";
            return;
        }

        var rotationText = RotationFormatter.FormatForPrompt(job, _recorder.EncounterName, events, start, end);
        var ct           = _analyzeCts.Token;

        // Fire-and-forget — PollAnalysisTask() in Draw() handles completion
        _analyzeTask = Task.Run(async () =>
        {
            var result = await _analyzer.AnalyzeAsync(
                rotationText, _plugin.Config.GeminiApiKey, ct);

            // Record usage only on success
            _plugin.Config.RecordGroundedQuery();
            return result;
        }, ct);
    }

    /// <summary>
    /// Polls the in-flight task each frame. Safe to call every Draw() —
    /// IsCompletedSuccessfully is a non-blocking property check.
    /// </summary>
    private void PollAnalysisTask()
    {
        if (_analyzeTask == null) return;

        if (_analyzeTask.IsCompletedSuccessfully)
        {
            _analyzeResult = _analyzeTask.Result;
            _analyzeError  = "";
            _analyzeTask   = null;
        }
        else if (_analyzeTask.IsFaulted)
        {
            _analyzeError  = _analyzeTask.Exception?.InnerException?.Message
                             ?? "Unknown error";
            _analyzeResult = "";
            _analyzeTask   = null;
        }
        else if (_analyzeTask.IsCanceled)
        {
            _analyzeTask = null;
        }
    }
}
