//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing;

public abstract class BlockTracerBase<TTrace, TTracer> : IBlockTracer where TTracer : class, ITxTracer
{
    private TTracer? _currentTxTracer;
    private readonly Keccak? _txHash;

    protected BlockTracerBase(Keccak? txHash = null) => _txHash = txHash;

    public virtual bool IsTracingRewards => false;

    protected List<TTrace> TxTraces => new();

    public IReadOnlyCollection<TTrace> BuildResult() => TxTraces.AsReadOnly();

    public abstract void EndBlockTrace();

    public virtual void EndTxTrace()
    {
        if (_currentTxTracer == null)
            return;

        var trace = OnEnd(_currentTxTracer);

        AddTrace(trace);

        _currentTxTracer = null;
    }

    public virtual void ReportReward(Address author, string rewardType, UInt256 rewardValue) { }

    public abstract void StartNewBlockTrace(Block block);

    public virtual ITxTracer StartNewTxTrace(Transaction? tx)
    {
        if (ShouldTraceTx(tx))
        {
            _currentTxTracer = OnStart(tx);

            return _currentTxTracer;
        }

        return NullTxTracer.Instance;
    }

    protected virtual void AddTrace(TTrace trace) => TxTraces.Add(trace);

    protected abstract TTrace OnEnd(TTracer txTracer);

    protected abstract TTracer OnStart(Transaction? tx);

    protected virtual bool ShouldTraceTx(Transaction? tx) => _txHash == null || _txHash == tx?.Hash;
}
