using System;
using System.Collections.Generic;

namespace EasyIsle;

/// <summary>One weather the island can have and its chance (percent) from the WeatherRate sheet.</summary>
public record WeatherChance(uint Id, string Name, string EnglishName, int Rate, int Icon);

/// <summary>A stretch of real time when something can be found.</summary>
public record SpawnWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public bool OpenAt(DateTimeOffset t) => t >= Start && t < End;
}

/// <summary>Eorzea time and the island's weather, worked out the same way the game does.</summary>
public static class IslandClock
{
    /// <summary>Real seconds per Eorzea hour.</summary>
    public const double RealSecondsPerHour = 175.0;

    /// <summary>Weather changes every 8 Eorzea hours = 1400 real seconds.</summary>
    public const long WeatherPeriod = 1400;

    public static double EorzeaHours(DateTimeOffset t) => t.ToUnixTimeMilliseconds() / 1000.0 / RealSecondsPerHour;

    public static int EorzeaHour(DateTimeOffset t) => (int)(Math.Floor(EorzeaHours(t)) % 24);

    public static string EorzeaClock(DateTimeOffset t)
    {
        var h = EorzeaHours(t);
        var hour = (int)(Math.Floor(h) % 24);
        var minute = (int)((h - Math.Floor(h)) * 60);
        return $"{(hour % 12 == 0 ? 12 : hour % 12)}:{minute:00} {(hour < 12 ? "AM" : "PM")}";
    }

    /// <summary>Real time when the Eorzea hour with this index (hours since epoch) begins.</summary>
    public static DateTimeOffset HourStart(long eorzeaHourIndex) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Ceiling(eorzeaHourIndex * RealSecondsPerHour * 1000));

    /// <summary>The game's weather roll (0-99) for the period containing this unix time.</summary>
    public static int WeatherTarget(long unixSeconds)
    {
        var bell = unixSeconds / 175;
        var increment = (uint)((bell + 8 - (bell % 8)) % 24);
        var totalDays = (uint)(unixSeconds / 4200);
        var calcBase = totalDays * 100 + increment;
        var step1 = (calcBase << 11) ^ calcBase;
        var step2 = (step1 >> 8) ^ step1;
        return (int)(step2 % 100);
    }

    public static WeatherChance? WeatherAt(IReadOnlyList<WeatherChance> rates, DateTimeOffset t)
    {
        if (rates.Count == 0) return null;
        var target = WeatherTarget(t.ToUnixTimeSeconds());
        var sum = 0;
        foreach (var w in rates)
        {
            sum += w.Rate;
            if (target < sum) return w;
        }

        return rates[^1];
    }

    public static DateTimeOffset PeriodStart(DateTimeOffset t) =>
        DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds() / WeatherPeriod * WeatherPeriod);

    /// <summary>The current weather period and the next ones.</summary>
    public static List<(DateTimeOffset Start, WeatherChance Weather)> Forecast(IReadOnlyList<WeatherChance> rates, DateTimeOffset now, int periods)
    {
        var list = new List<(DateTimeOffset, WeatherChance)>();
        var start = PeriodStart(now);
        for (var i = 0; i < periods; i++)
        {
            var t = start.AddSeconds(i * WeatherPeriod);
            if (WeatherAt(rates, t) is { } w) list.Add((t, w));
        }

        return list;
    }

    /// <summary>"9 PM-12 AM" -> [21, 24). Null when there's no time limit or it can't be read.</summary>
    public static (int From, int To)? ParseHours(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return null;
        var a = ParseHour(parts[0]);
        var b = ParseHour(parts[1]);
        if (a == null || b == null) return null;
        var to = b.Value <= a.Value ? b.Value + 24 : b.Value;
        return (a.Value, to);
    }

    private static int? ParseHour(string s)
    {
        s = s.Trim().ToUpperInvariant();
        var pm = s.EndsWith("PM");
        var am = s.EndsWith("AM");
        if (!pm && !am) return null;
        if (!int.TryParse(s[..^2].Trim(), out var h) || h < 1 || h > 12) return null;
        return (h % 12) + (pm ? 12 : 0);
    }

    private static bool HourIn(int hour, (int From, int To) range) =>
        (hour >= range.From && hour < range.To) || (hour + 24 >= range.From && hour + 24 < range.To);

    /// <summary>
    /// The window (open now, or the next one) when a spot's time and weather both hold, searching up to
    /// a few real days ahead. Null if it never opens in that time, or the weather table isn't loaded.
    /// Spots with no conditions return an always-open window.
    /// </summary>
    public static SpawnWindow? NextWindow(SpeciesSpot spot, IReadOnlyList<WeatherChance> rates, DateTimeOffset now, int searchHours = 24 * 80)
    {
        var hours = ParseHours(spot.Time);
        var needWeather = spot.Weather.Length > 0;
        if (hours == null && !needWeather) return new SpawnWindow(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        if (needWeather && rates.Count == 0) return null;

        bool Ok(long index)
        {
            var start = HourStart(index);
            if (hours is { } r && !HourIn((int)(index % 24), r)) return false;
            if (needWeather)
            {
                var w = WeatherAt(rates, start);
                if (w == null || !w.EnglishName.Equals(spot.Weather, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        var first = (long)Math.Floor(EorzeaHours(now));
        for (var i = first; i < first + searchHours; i++)
        {
            if (!Ok(i)) continue;
            var end = i + 1;
            while (end < i + 24 * 7 && Ok(end)) end++;
            return new SpawnWindow(i == first ? HourStart(i) : HourStart(i), HourStart(end));
        }

        return null;
    }

    public static string Span(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return "under a minute";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    /// <summary>"Can appear now (for 12m more)" / "Next in 1h 5m (for 9m)".</summary>
    public static string Describe(SpawnWindow? w, DateTimeOffset now, bool rare)
    {
        if (!rare) return "Always around.";
        if (w == null) return "Weather forecast not loaded yet.";
        if (w.OpenAt(now)) return $"Can appear now ({Span(w.End - now)} left).";
        return $"Next chance in {Span(w.Start - now)} (lasts {Span(w.End - w.Start)}).";
    }
}
