// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.StateComposition;

public readonly record struct ScanProgressResult
{
    public bool IsScanning { get; init; }
    public double Progress { get; init; }
    public long? EstimatedAccountsRemaining { get; init; }
    public TimeSpan? ElapsedTime { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
}
