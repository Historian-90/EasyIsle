using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyIsle;

/// <summary>Where and when an island animal can be found (map coordinates on the island).</summary>
public record SpeciesSpot(string Name, char Size, string Common, string Bonus, string Time, string Weather, float X, float Y)
{
    public bool Rare => Time.Length > 0 || Weather.Length > 0;

    public string When => (Time, Weather) switch
    {
        ("", "") => "any time",
        (var t, "") => $"{t} (Eorzea time)",
        ("", var w) => w,
        var (t, w) => $"{t} (Eorzea time) in {w}",
    };
}

/// <summary>
/// The island's animals: names, spawn conditions and map spots (consolegameswiki Island Pasture).
/// The game data has each animal's leavings and size but not its name or spawn spot, so this table
/// fills those in. Leavings come from the game data; the table is matched to it by leavings + size.
/// English names only; other clients fall back to the leavings.
/// </summary>
public static class SpeciesTable
{
    public static readonly SpeciesSpot[] All =
    [
        // Always around.
        new("Apkallu", 'S', "Fleece", "Egg", "", "", 19, 11),
        new("Aurochs", 'L', "Milk", "Horn", "", "", 12, 17),
        new("Blue Back", 'L', "Egg", "Feather", "", "", 28, 28),
        new("Chocobo", 'M', "Fur", "Feather", "", "", 20, 21),
        new("Coblyn", 'S', "Fang", "Carapace", "", "", 20, 13),
        new("Glyptodon Pup", 'M', "Carapace", "Claw", "", "", 30, 11),
        new("Ground Squirrel", 'S', "Claw", "Fur", "", "", 15, 19),
        new("Island Doe", 'M', "Fur", "Milk", "", "", 21, 19),
        new("Island Nanny", 'L', "Milk", "Horn", "", "", 25, 24),
        new("Lost Lamb", 'S', "Fleece", "Milk", "", "", 20, 23),
        new("Opo-opo", 'S', "Claw", "Fur", "", "", 20, 26),
        new("Quartz Spriggan", 'S', "Fang", "Fur", "", "", 26.8f, 20.9f),
        new("Wild Boar", 'L', "Fang", "Horn", "", "", 16, 16),
        new("Wild Dodo", 'M', "Egg", "Feather", "", "", 16, 12),

        // Rare: time of day (Eorzea time).
        new("Amethyst Spriggan", 'S', "Fur", "Fang", "9 PM-12 AM", "", 28, 16),
        new("Apkallu of Paradise", 'S', "Egg", "Fleece", "12 PM-3 PM", "", 19, 11),
        new("Dodo of Paradise", 'M', "Feather", "Egg", "3 PM-6 PM", "", 16, 12),
        new("Glyptodon", 'M', "Claw", "Carapace", "12 AM-3 AM", "", 31, 11),
        new("Island Billy", 'L', "Horn", "Fleece", "3 AM-6 AM", "", 26, 22),
        new("Lemur", 'S', "Fur", "Claw", "6 AM-9 AM", "", 20, 26),
        new("Stag", 'M', "Fur", "Horn", "6 PM-9 PM", "", 20, 19),
        new("Star Marmot", 'S', "Fur", "Claw", "9 AM-12 PM", "", 15, 19),

        // Rare: weather.
        new("Black Chocobo", 'M', "Feather", "Fur", "", "Clear Skies", 13, 11),
        new("Boar of Paradise", 'L', "Horn", "Fang", "", "Showers", 16, 16),
        new("Gold Back", 'L', "Feather", "Egg", "", "Rain", 31, 28),
        new("Grand Buffalo", 'L', "Horn", "Milk", "", "Clouds", 12, 17),
        new("Ornery Karakul", 'S', "Milk", "Fleece", "", "Fair Skies", 20, 23),
        new("Yellow Coblyn", 'S', "Carapace", "Fang", "", "Fog", 27, 19),

        // Specials: time and weather.
        new("Adamantoise", 'L', "Carapace", "Claw", "12 PM-3 PM", "Fog", 31.8f, 12.2f),
        new("Alkonost", 'M', "Feather", "Egg", "9 PM-12 AM", "Clear Skies", 23, 30.5f),
        new("Alligator", 'L', "Claw", "Fang", "6 AM-9 AM", "Showers", 17, 24),
        new("Beachcomb", 'S', "Carapace", "Claw", "12 AM-3 AM", "Rain", 17, 12),
        new("Funguar", 'S', "Fleece", "Fur", "3 PM-6 PM", "Rain", 20, 27),
        new("Goobbue", 'L', "Fang", "Claw", "9 AM-12 PM", "Clouds", 33, 16),
        new("Grand Doblyn", 'L', "Fang", "Carapace", "3 AM-6 AM", "Fair Skies", 24.7f, 20.8f),
        new("Morbol", 'L', "Fang", "Carapace", "9 PM-12 AM", "Showers", 29.3f, 23.4f),
        new("Morbol Seedling", 'S', "Carapace", "Fang", "3 AM-6 AM", "Clouds", 19, 19),
        new("Paissa", 'M', "Claw", "Fleece", "12 PM-3 PM", "Fair Skies", 24, 28),
        new("Pteranodon", 'L', "Claw", "Horn", "9 AM-12 PM", "Clear Skies", 25.1f, 16),
        new("Tiger of Paradise", 'M', "Fang", "Fur", "6 PM-9 PM", "Fair Skies", 15, 14),
        new("Twinklefleece", 'S', "Fleece", "Fur", "6 PM-9 PM", "Fog", 22, 20),
        new("Weird Spriggan", 'L', "Fang", "Fur", "12 AM-3 AM", "Fog", 24, 18),
    ];

    /// <summary>Table entries with these leavings ("Sanctuary Fang" etc.), preferring the right size.</summary>
    public static List<SpeciesSpot> Match(string common, string bonus, char size)
    {
        static string Key(string n) => n.Replace("Sanctuary ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var c = Key(common);
        var b = Key(bonus);
        var hits = All.Where(s => s.Common.Equals(c, StringComparison.OrdinalIgnoreCase) && s.Bonus.Equals(b, StringComparison.OrdinalIgnoreCase)).ToList();
        var sized = hits.Where(s => s.Size == size).ToList();
        return sized.Count > 0 ? sized : hits;
    }
}
