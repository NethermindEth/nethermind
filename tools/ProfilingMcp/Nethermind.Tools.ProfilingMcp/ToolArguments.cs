// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

namespace Nethermind.Tools.ProfilingMcp;

/// <summary>Checks and converts tool arguments shared by several tools.</summary>
internal static class ToolArguments
{
    public const int MaxSeconds = 600;

    public const string SecondsDescription = "Seconds to sample, 1 to 600.";

    /// <summary>A pattern from the caller is matched against many names, so one match may not run away.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Checks a sampling window: positive, and short enough not to keep a profiler attached to a production process indefinitely.</summary>
    public static TimeSpan Window(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, MaxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    public static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant, MatchTimeout);

    public static Regex? OptionalPattern(string? pattern) => pattern is null ? null : Pattern(pattern);
}
