// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public abstract class BlockTracerBase<TTrace, TTracer> : IBlockTracer<TTrace> where TTracer : class, ITxTracer
{
    private readonly Keccak? _txHash;

    protected BlockTracerBase()
    {
        TxTraces = new ResettableList<TTrace>();
    }

    protected BlockTracerBase(Keccak? txHash)
    {
        _txHash = txHash;
        TxTraces = new ResettableList<TTrace>();
    }

    private TTracer? CurrentTxTracer { get; set; }

    protected abstract TTracer OnStart(Transaction? tx);
    protected abstract TTrace OnEnd(TTracer txTracer);

    public virtual bool IsTracingRewards => false;

    public virtual void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
    }

    public virtual void StartNewBlockTrace(Block block)
    {
        TxTraces.Reset();
    }

    ITxTracer IBlockTracer.StartNewTxTrace(Transaction? tx)
    {
        if (ShouldTraceTx(tx))
        {
            CurrentTxTracer = OnStart(tx);
            return CurrentTxTracer;
        }

        return NullTxTracer.Instance;
    }

    void IBlockTracer.EndTxTrace()
    {
        if (CurrentTxTracer is null)
            return;

        AddTrace(OnEnd(CurrentTxTracer));

        CurrentTxTracer = null;
    }

    public virtual void EndBlockTrace() { }

    protected virtual bool ShouldTraceTx(Transaction? tx)
    {
        return _txHash is null || tx?.Hash == _txHash;
    }

    protected ResettableList<TTrace> TxTraces { get; }

    public IReadOnlyCollection<TTrace> BuildResult() => TxTraces;

    protected virtual void AddTrace(TTrace trace) => TxTraces.Add(trace);
}
