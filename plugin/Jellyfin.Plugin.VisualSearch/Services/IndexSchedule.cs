using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VisualSearch;

internal sealed record IndexScheduleWindow(DayOfWeek Day, TimeSpan Start, TimeSpan End)
{
    public string Key => $"{Day}:{Start.ToString(@"hh\:mm")}-{End.ToString(@"hh\:mm")}";
}

internal static class IndexSchedule
{
    private static readonly Regex RangePattern = new(
        @"(?<start>\d{1,2}:\d{2})\s*[-~至到]\s*(?<end>\d{1,2}:\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<IndexScheduleWindow> Parse(string? specification)
    {
        var result = new List<IndexScheduleWindow>();
        if (string.IsNullOrWhiteSpace(specification)) return result;
        foreach (var rawLine in specification.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var split = line.IndexOfAny(new[] { ' ', '\t', ':', '=' });
            if (split <= 0) continue;
            var day = ParseDay(line[..split].Trim().TrimEnd(':', '='));
            if (!day.HasValue) continue;
            var ranges = line[split..];
            foreach (Match match in RangePattern.Matches(ranges))
            {
                if (!TimeSpan.TryParse(match.Groups["start"].Value, CultureInfo.InvariantCulture, out var start)
                    || !TimeSpan.TryParse(match.Groups["end"].Value, CultureInfo.InvariantCulture, out var end)
                    || start < TimeSpan.Zero || end < TimeSpan.Zero
                    || start >= TimeSpan.FromDays(1) || end >= TimeSpan.FromDays(1)) continue;
                result.Add(new IndexScheduleWindow(day.Value, start, end));
            }
        }
        return result
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Day)
            .ThenBy(x => x.Start)
            .ToArray();
    }

    public static (IndexScheduleWindow Window, DateTime StartAt)? FindActive(DateTime localNow, IReadOnlyList<IndexScheduleWindow> windows)
    {
        foreach (var window in windows)
        {
            var today = localNow.Date;
            if (window.Day == localNow.DayOfWeek)
            {
                var startAt = today + window.Start;
                var endAt = today + window.End;
                if (window.End > window.Start && localNow >= startAt && localNow < endAt) return (window, startAt);
                if (window.End <= window.Start && localNow >= startAt) return (window, startAt);
            }

            var previousDay = localNow.AddDays(-1).DayOfWeek;
            if (window.End <= window.Start && window.Day == previousDay)
            {
                var startAt = today.AddDays(-1) + window.Start;
                var endAt = today + window.End;
                if (localNow >= endAt.Date && localNow < endAt) return (window, startAt);
            }
        }
        return null;
    }

    private static DayOfWeek? ParseDay(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "周一" or "星期一" or "一" or "mon" or "monday" => DayOfWeek.Monday,
            "周二" or "星期二" or "二" or "tue" or "tuesday" => DayOfWeek.Tuesday,
            "周三" or "星期三" or "三" or "wed" or "wednesday" => DayOfWeek.Wednesday,
            "周四" or "星期四" or "四" or "thu" or "thursday" => DayOfWeek.Thursday,
            "周五" or "星期五" or "五" or "fri" or "friday" => DayOfWeek.Friday,
            "周六" or "星期六" or "六" or "sat" or "saturday" => DayOfWeek.Saturday,
            "周日" or "星期日" or "周天" or "星期天" or "日" or "天" or "sun" or "sunday" => DayOfWeek.Sunday,
            _ => null
        };
    }
}
