// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

/// <summary>
/// Wraps cached stats with a staleness indicator.
/// Stats is null before the first scan completes.
/// </summary>
public class CachedStatsResponse
{
    public StateCompositionStats? Stats { get; init; }

    /// <summary>
    /// Number of blocks processed since baseline scan.
    /// Higher values indicate staler data.
    /// </summary>
    public long BlocksSinceBaseline { get; init; }
}
