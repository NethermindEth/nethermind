// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// Counts the calls that reach the wrapped precompile.
/// </summary>
public sealed class MeteredPrecompile : PrecompileDecorator
{
    private static readonly ConcurrentBag<MeteredPrecompile> Metered = [];

    // all metered precompiles are allocated adjacently, so unpadded counters would share CPU cache lines
    private CacheLinePaddedLong _runs;

    public MeteredPrecompile(IPrecompile inner) : base(inner) => Metered.Add(this);

    public static void PublishAll()
    {
        foreach (MeteredPrecompile precompile in Metered)
            Metrics.PrecompileRuns[precompile.Name] = Volatile.Read(ref precompile._runs.Value);
    }

    public override Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Interlocked.Increment(ref _runs.Value);
        return base.Run(inputData, releaseSpec);
    }
}
