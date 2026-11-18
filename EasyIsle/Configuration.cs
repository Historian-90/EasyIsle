using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace EasyIsle;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Last isleventory counts seen (MJIItemPouch row -> count), used when the game isn't showing them.</summary>
    public Dictionary<uint, int> LastIsleventory { get; set; } = [];

    public DateTime LastIsleventoryUtc { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
