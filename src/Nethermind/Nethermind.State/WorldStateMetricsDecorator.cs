// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using EvmMetrics = Nethermind.Evm.Metrics;

namespace Nethermind.State;

public class WorldStateMetricsDecorator(IWorldState innerWorldState) : WrappedWorldState(innerWorldState)
{
    public double StateMerkleizationTime { get; private set; }

    public override void Reset(bool resetBlockChanges = true)
    {
        StateMerkleizationTime = 0d;
        _innerWorldState.Reset(resetBlockChanges);
    }

    public override void RecalculateStateRoot()
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.RecalculateStateRoot();
        long ticks = Stopwatch.GetElapsedTime(start).Ticks;
        StateMerkleizationTime += ticks / (double)TimeSpan.TicksPerMillisecond;
        EvmMetrics.IncrementStateHashTime(ticks);
    }

    public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.Commit(releaseSpec, tracer, isGenesis, commitRoots);
        long ticks = Stopwatch.GetElapsedTime(start).Ticks;
        if (commitRoots)
        {
            StateMerkleizationTime += ticks / (double)TimeSpan.TicksPerMillisecond;
            EvmMetrics.IncrementStateHashTime(ticks);
        }
        else
        {
            EvmMetrics.IncrementCommitTime(ticks);
        }
    }

    public override void CommitTree(long blockNumber)
    {
        long start = Stopwatch.GetTimestamp();
        _innerWorldState.CommitTree(blockNumber);
        long ticks = Stopwatch.GetElapsedTime(start).Ticks;
        StateMerkleizationTime += ticks / (double)TimeSpan.TicksPerMillisecond;
        EvmMetrics.IncrementStateHashTime(ticks);
    }
}
