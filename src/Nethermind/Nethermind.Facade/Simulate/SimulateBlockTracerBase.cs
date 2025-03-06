// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockTracerBase<TxTracer, TTrace> : IBlockTracer<SimulateBlockResult<TTrace>> where TxTracer : class, ITxTracer
{
    private readonly bool _includeFullTxData;
    private readonly ISpecProvider _spec;
    protected readonly bool _isTracingLogs;

    public SimulateBlockTracerBase(bool isTracingLogs, bool includeFullTxData, ISpecProvider spec)
    {
        _includeFullTxData = includeFullTxData;
        _spec = spec;
        _isTracingLogs = isTracingLogs;
    }

    protected readonly List<TxTracer> _txTracers = new();
    public bool IsTracingRewards => false;
    protected Block _currentBlock = null!;
    public List<SimulateBlockResult<TTrace>> Results { get; } = new();

    public virtual void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public Type GetTxTracerType() => typeof(TxTracer);

    public void StartNewBlockTrace(Block block)
    {
        _txTracers.Clear();
        _currentBlock = block;
    }

    public virtual ITxTracer StartNewTxTrace(Transaction? tx) =>  NullTxTracer.Instance;

    protected virtual IReadOnlyList<TTrace> getCalls() => [];

    public void EndBlockTrace()
    {
        SimulateBlockResult<TTrace>? result = new(_currentBlock, _includeFullTxData, _spec)
        {
            Calls = getCalls(),
        };

        Results.Add(result);
    }

    public IReadOnlyList<SimulateBlockResult<TTrace>> BuildResult() => Results;

    public void EndTxTrace() { }
}


public interface ISimulateBlockTracerBaseFactory<out TTracer, in TxTracer, in TTrace>
    where TTracer : SimulateBlockTracerBase<TxTracer, TTrace>
    where TxTracer : class, ITxTracer
{
    TTracer Create(bool isTracingLogs, bool includeFullTxData, ISpecProvider spec);
}
