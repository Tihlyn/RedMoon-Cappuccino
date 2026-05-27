// ================================================================
// INTEGRATION GUIDE — what to add to your existing Plugin.cs
// ================================================================
//
// This is not a standalone file — it shows the snippets to merge
// into your existing plugin entry point.
// ================================================================

// 1. ADD THESE FIELDS to your Plugin class:
//
//    private ActionRecorder  _actionRecorder  = null!;
//    private GeminiAnalyzer  _geminiAnalyzer  = null!;
//    private RecorderWindow  _recorderWindow  = null!;


// 2. ADD THESE SERVICES to your [PluginService] block (if not already present):
//
//    [PluginService] internal static IGameInteropProvider GameInterop  { get; private set; } = null!;
//    [PluginService] internal static IObjectTable         ObjectTable  { get; private set; } = null!;
//    [PluginService] internal static IDataManager         DataManager  { get; private set; } = null!;
//    [PluginService] internal static IClientState         ClientState  { get; private set; } = null!;
//    [PluginService] internal static IPluginLog           Log          { get; private set; } = null!;
//
//    NOTE: IObjectTable.LocalPlayer is the v15-correct way to get the local player.
//    Do NOT use IClientState.LocalPlayer — it was removed in v15.


// 3. ADD TO YOUR CONSTRUCTOR, after existing setup:
//
//    _actionRecorder = new ActionRecorder(GameInterop, ObjectTable, DataManager, ClientState, Log);
//    _geminiAnalyzer = new GeminiAnalyzer();
//    _recorderWindow = new RecorderWindow(this, _actionRecorder, _geminiAnalyzer);
//    WindowSystem.AddWindow(_recorderWindow);


// 4. ADD TO YOUR Dispose() METHOD, before base dispose:
//
//    _recorderWindow.Dispose();
//    _geminiAnalyzer.Dispose();
//    _actionRecorder.Dispose();
//    WindowSystem.RemoveWindow(_recorderWindow);


// 5. ADD A SLASH COMMAND (optional) to toggle the window:
//
//    CommandManager.AddHandler("/dpscheck", new CommandInfo(OnCommand)
//    {
//        HelpMessage = "Toggle the rotation recorder window."
//    });
//
//    private void OnCommand(string command, string args)
//        => _recorderWindow.Toggle();
//
//    // And in Dispose():
//    CommandManager.RemoveHandler("/dpscheck");


// 6. ADD TO YOUR ConfigWindow.Draw() for the API key input:
//
//    ImGui.Separator();
//    ImGui.TextUnformatted("Rotation Analyser — Gemini API Key");
//    ImGui.SameLine();
//    // HelpMarker requires: using Dalamud.Interface.Components;
//    ImGuiComponents.HelpMarker(
//        "Free key at aistudio.google.com\n" +
//        "Model: gemini-2.5-flash-lite\n" +
//        "500 grounded RPD free (plugin caps at 450)");
//
//    var keyBuf = Config.GeminiApiKey;
//    if (ImGui.InputText("##geminikey", ref keyBuf, 200, ImGuiInputTextFlags.Password))
//    {
//        Config.GeminiApiKey = keyBuf;
//        Config.Save();
//    }


// ================================================================
// NAMESPACE REMINDER
// ================================================================
// All five files use:  namespace YourPlugin.RotationRecorder;
// Replace "YourPlugin" with your actual plugin namespace throughout.
// ================================================================
