// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Metrics;

public enum MetricsReportFormat
{
    Pretty, Json, Html
}

public sealed record TimeMetrics
{
    public required TimeSpan Max { get; init; }
    public required TimeSpan Min { get; init; }
    public required TimeSpan Average { get; init; }
    public required TimeSpan StandardDeviation { get; init; }

    public static TimeMetrics From(List<TimeSpan> times)
    {
        if (times.Count == 0)
        {
            return new TimeMetrics
            {
                Max = TimeSpan.Zero,
                Min = TimeSpan.Zero,
                Average = TimeSpan.Zero,
                StandardDeviation = TimeSpan.Zero
            };
        }

        return new TimeMetrics
        {
            Max = times.Max(),
            Min = times.Min(),
            Average = TimeSpan.FromTicks((long)times.Average(t => t.Ticks)),
            StandardDeviation = StdDev(times)
        };

        static TimeSpan StdDev(List<TimeSpan> times)
        {
            double average = times.Average(t => t.Ticks);
            double sumOfSquares = times.Sum(t => Math.Pow(t.Ticks - average, 2));
            var stdDev = Math.Sqrt(sumOfSquares / times.Count);
            return TimeSpan.FromTicks((long)stdDev);
        }
    }
}

public sealed record MetricsReport
{
    public required long TotalMessages { get; init; }
    public required long Failed { get; init; }
    public required long Succeeded { get; init; }
    public required long Ignored { get; init; }
    public required long Responses { get; init; }
    public required TimeSpan TotalTime { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, TimeSpan>> Singles { get; init; }
    public required IReadOnlyDictionary<string, TimeSpan> Batches { get; init; }

    private Dictionary<string, TimeMetrics>? _singlesMetrics;
    public Dictionary<string, TimeMetrics> SinglesMetrics => _singlesMetrics ??= Singles.ToDictionary(kvp => kvp.Key, kvp => TimeMetrics.From(kvp.Value.Values.ToList()));

    private TimeMetrics? _batchesMetrics;
    public TimeMetrics BatchesMetrics => _batchesMetrics ??= TimeMetrics.From(Batches.Values.ToList());
}

public interface IMetricsReportProvider
{
    MetricsReport Report();
}
