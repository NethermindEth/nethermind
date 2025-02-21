using System;
using System.Collections.Generic;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Block = Nethermind.Core.Block;
using Nethermind.Int256;
using Transaction = Nethermind.Core.Transaction;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;

namespace Nethermind.Facade;

public class TraceSimulateTracer<TTrace>(
    IBlockTracer<TTrace> inner, 
    ISpecProvider specProvider
) : IBlockTracer<SimulateBlockResult<TTrace>>
{
    private readonly List<SimulateBlockResult<TTrace>> _results = new();
    private Block _currentBlock;

    public IReadOnlyList<SimulateBlockResult<TTrace>> BuildResult() => _results;
    public bool IsTracingRewards => inner.IsTracingRewards;

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        inner.ReportReward(author, rewardType, rewardValue);
    }

    public void StartNewBlockTrace(Block block)
    {
        _currentBlock = block;
        inner.StartNewBlockTrace(block);
    }

    public ITxTracer StartNewTxTrace(Transaction? tx) => inner.StartNewTxTrace(tx);
    public void EndTxTrace() => inner.EndTxTrace();

    public void EndBlockTrace()
    {
        inner.EndBlockTrace();
        _results.Add(new SimulateBlockResult<TTrace>(
            _currentBlock, 
            false, 
            specProvider
        ) 
        { 
            Calls = inner.BuildResult() 
        });
    }
}
