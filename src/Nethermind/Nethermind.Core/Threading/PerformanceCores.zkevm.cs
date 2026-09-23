// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Threading;

/// <summary>
/// Nothing to narrow for the zkVM guest, which is single-threaded and has no core types - see the std counterpart.
/// </summary>
public static partial class PerformanceCores
{
    public static ReadOnlySpan<int> Cpus => [];

    public static Scope NarrowCurrentThread() => default;

    public readonly struct Scope : IDisposable
    {
        public void Dispose() { }
    }
}
