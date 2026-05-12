using System;
using Dalamud.Configuration;

namespace __MYPLUGIN__;

[Serializable]
public class Configuration : IPluginConfiguration
{
    // Bump this and add a migration block in your Plugin constructor when
    // you make backward-incompatible changes to the layout below.
    public int Version { get; set; } = 0;

    // Example settings — replace with your plugin's real options.
    public bool ShowOnLogin           { get; set; } = true;
    public bool IsConfigWindowMovable { get; set; } = true;

    /// <summary>
    /// Persists the configuration to
    /// %AppData%\XIVLauncher\pluginConfigs\__MYPLUGIN__.json.
    /// Newtonsoft.Json with TypeNameHandling.Objects is used internally,
    /// so polymorphic fields round-trip correctly.
    /// </summary>
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
