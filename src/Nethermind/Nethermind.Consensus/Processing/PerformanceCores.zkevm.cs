// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Nothing to narrow for the zkVM guest, which is single-threaded and has no core types - see the std counterpart.
/// </summary>
internal static partial class PerformanceCores
{
    public static ReadOnlySpan<int> Cpus(ProcessingCores cores) => [];

    public static Scope NarrowCurrentThread(ProcessingCores cores, ILogger logger) => default;

    public static PrewarmSplit? Prewarm => null;

    internal sealed class PrewarmSplit
    {
        public int NearWorkers => 0;

        public Scope NarrowNear(ILogger logger) => default;

        public Scope NarrowFar(ILogger logger) => default;
    }

    public readonly struct Scope : IDisposable
    {
        public void Dispose() { }
    }
}
