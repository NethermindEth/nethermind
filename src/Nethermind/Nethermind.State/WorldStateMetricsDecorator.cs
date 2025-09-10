// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;

namespace Nethermind.State;

public class WorldStateMetricsDecorator(IWorldState innerWorldState) : WrappedWorldState(innerWorldState)
{
    public override void Reset(bool resetBlockChanges = true)
    {
        StateMerkleizationTime = 0d;
        _innerWorldState.Reset(resetBlockChanges);
    }

    public override void RecalculateStateRoot()
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.RecalculateStateRoot();
        StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public double StateMerkleizationTime { get; private set; }

    public override void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true)
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.Commit(releaseSpec, isGenesis, commitRoots);
        if (commitRoots)
            StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.Commit(releaseSpec, tracer, isGenesis, commitRoots);
        if (commitRoots)
            StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public override void CommitTree(long blockNumber)
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.CommitTree(blockNumber);
        StateMerkleizationTime += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
}
