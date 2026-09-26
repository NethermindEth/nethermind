// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.State.Pbt.Image;

/// <summary>Reports the long image phases on a wall-clock interval.</summary>
/// <remarks>
/// An image phase streams tens of millions of records with no natural block boundary, so the caller only counts
/// and a timer decides when a line is due. Disposing the reporter logs the phase's final line.
/// </remarks>
internal static class PbtImageProgress
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <param name="total">The expected record count, or zero when the phase cannot know it in advance.</param>
    public static ProgressReporter Start(string phase, string unit, ulong total, ILogManager logManager)
    {
        ProgressReporter reporter = new(phase, logManager, total, Interval);
        reporter.Logger.SetFormat(progress => Format(phase, unit, progress));
        return reporter;
    }

    /// <summary>Renders the counted records and their rate, preceded by a meter once the total is known.</summary>
    public static string Format(string phase, string unit, ProgressLogger progress)
    {
        string counted = $"{progress.CurrentValue,15:N0} {unit} ({progress.CurrentPerSecond,8:N0}/s)";
        if (progress.TargetValue == 0) return $"{phase} | {counted}";
        float percentage = Math.Clamp(progress.CurrentValue / (float)progress.TargetValue, 0, 1);
        return $"{phase} {percentage.ToString("P2", CultureInfo.InvariantCulture),8} {Progress.GetMeter(percentage, 1)} | {counted}";
    }
}
