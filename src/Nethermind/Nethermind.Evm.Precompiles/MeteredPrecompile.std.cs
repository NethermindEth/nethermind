// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// Counts the calls that reach the wrapped precompile.
/// </summary>
public sealed partial class MeteredPrecompile(IPrecompile inner) : PrecompileDecorator(inner)
{
    // all metered precompiles are allocated adjacently, so unpadded counters would share CPU cache lines
    private CacheLinePaddedLong _runs;

    /// <summary> Copies this precompile's call count into the exported metrics. </summary>
    internal void PublishMetrics() => Metrics.PrecompileRuns[Name] = Volatile.Read(ref _runs.Value);

    public override Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Interlocked.Increment(ref _runs.Value);
        return base.Run(inputData, releaseSpec);
    }
}
