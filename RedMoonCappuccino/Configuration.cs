using System;
using Dalamud.Configuration;

namespace RedMoonCappuccino;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool ShowOnLogin           { get; set; } = true;
    public bool IsConfigWindowMovable { get; set; } = true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
