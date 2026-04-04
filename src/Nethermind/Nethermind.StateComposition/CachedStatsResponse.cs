// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.StateComposition;

/// <summary>
/// Wraps cached stats for a specific (or most recent) scan,
/// plus metadata listing all available cached scans.
/// </summary>
public readonly record struct CachedStatsResponse
{
    public StateCompositionStats? Stats { get; init; }
    public IReadOnlyList<ScanMetadata>? AvailableScans { get; init; }
}
