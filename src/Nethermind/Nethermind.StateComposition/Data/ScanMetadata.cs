// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition.Data;

/// <summary>
/// Metadata for the most recent full scan. The default value (<see cref="IsComplete"/> == false)
/// represents "no scan has finished yet" — consumers gate on <see cref="IsComplete"/>
/// instead of a nullable wrapper so the type stays a flat record struct.
/// </summary>
public readonly record struct ScanMetadata
{
    public long BlockNumber { get; init; }
    public Hash256 StateRoot { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsComplete { get; init; }
}
