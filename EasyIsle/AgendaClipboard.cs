using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace EasyIsle;

/// <summary>
/// Puts a workshop agenda into the game's own agenda clipboard (the one its "Copy Agenda" fills),
/// so the player can paste it with "Apply Copied Agenda". Nothing is scheduled by the plugin.
/// Layout matches what the game writes when copying (checked in game, 0.3.1 readout):
/// count, efficient count, entry order bytes, then entries with flags 0x02 (in a chain),
/// 0x20 (another entry follows) and 0x80 (efficient), and a bitmask of the hours used.
/// </summary>
public static unsafe class AgendaClipboard
{
    private const uint InChain = 0x02;
    private const uint HasNext = 0x20;
    private const uint Efficient = 0x80;

    public static bool Available
    {
        get
        {
            var agent = AgentMJICraftSchedule.Instance();
            return agent != null && agent->Data != null && agent->IsAgentActive();
        }
    }

    public static string? Copy(IReadOnlyList<PlannedCraft> crafts)
    {
        if (!Available) return "Open the Isleworks agenda first.";
        if (crafts.Count == 0 || crafts.Count > 6) return "That agenda can't be copied.";

        var data = AgentMJICraftSchedule.Instance()->Data;
        var target = &data->CopiedSchedule;
        new Span<byte>(target, sizeof(AgentMJICraftSchedule.WorkshopData)).Clear();

        var bytes = (byte*)target;
        uint slots = 0;
        byte efficientCount = 0;
        for (var i = 0; i < crafts.Count; i++)
        {
            var c = crafts[i];
            if (c.StartHour + c.Craft.Hours > 24) return "That agenda runs past 24 hours.";
            bytes[2 + i] = (byte)i;

            ref var e = ref target->EntryData[i];
            e.CraftObjectId = c.Craft.Id;
            var flags = InChain;
            if (i < crafts.Count - 1) flags |= HasNext;
            if (c.Efficient)
            {
                flags |= Efficient;
                efficientCount++;
            }

            e.Flags = (AgentMJICraftSchedule.EntryFlags)flags;
            e.StartingSlot = (byte)c.StartHour;
            e.Duration = (byte)c.Craft.Hours;
            e.Started = false;
            e.Efficient = c.Efficient;
            for (var h = c.StartHour; h < c.StartHour + c.Craft.Hours; h++) slots |= 1u << h;
        }

        target->NumScheduleEntries = (byte)crafts.Count;
        target->NumEfficientCrafts = efficientCount;
        target->UsedTimeSlots = slots;
        return null;
    }
}
