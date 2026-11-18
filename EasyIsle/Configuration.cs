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

    /// <summary>Season start (yyyy-MM-dd, Tuesday reset) -> cycle (0-6) -> craft id -> supply/demand byte.</summary>
    public Dictionary<string, Dictionary<int, Dictionary<uint, int>>> SupplyHistory { get; set; } = [];

    /// <summary>Expedition areas as last seen in the granary window (unlocked ones only).</summary>
    public List<Destination> KnownDestinations { get; set; } = [];

    public bool AllowGathering { get; set; } = true;

    public bool PrioritizeFavors { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
