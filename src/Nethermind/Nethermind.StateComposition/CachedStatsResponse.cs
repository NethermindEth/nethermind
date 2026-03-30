// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

/// <summary>
/// Wraps cached stats from last completed scan.
/// Stats are null before the first scan completes.
/// </summary>
public readonly record struct CachedStatsResponse
{
    public StateCompositionStats? Stats { get; init; }
}
