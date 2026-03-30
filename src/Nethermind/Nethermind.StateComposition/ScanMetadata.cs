// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

public readonly record struct ScanMetadata
{
    public long BlockNumber { get; init; }
    public Hash256? StateRoot { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsComplete { get; init; }
}
