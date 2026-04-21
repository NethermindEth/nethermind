// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.BalRecorder;

public class BalRecordingBlockProcessor(
    IBlockProcessor inner,
    IRecordedBalStore store,
    IWorldState stateProvider,
    BalRecorderSpecSwitch balSwitch,
    ILogManager logManager) : IBlockProcessor
{
    private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder is { } b ? b
        : store.RecordingEnabled ? WarnNullBuilder(logManager) : null;

    private static IBlockAccessListBuilder? WarnNullBuilder(ILogManager logManager)
    {
        logManager.GetClassLogger<BalRecordingBlockProcessor>()
            .Warn("RecordingEnabled is true but IWorldState does not implement IBlockAccessListBuilder — BAL recording is disabled.");
        return null;
    }

    public event Action? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        if (store.ReplayEnabled && suggestedBlock.BlockAccessList is null && suggestedBlock.Hash is not null)
            suggestedBlock.BlockAccessList = store.Get(suggestedBlock.Number, suggestedBlock.Hash);

        bool shouldFlip = ShouldFlip(suggestedBlock);
        if (shouldFlip) balSwitch.Enabled = true;
        try
        {
            (Block block, TxReceipt[] receipts) = inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
            if (store.RecordingEnabled && _balBuilder is not null)
                store.Insert(block, _balBuilder.GeneratedBlockAccessList);
            return (block, receipts);
        }
        finally
        {
            if (shouldFlip) balSwitch.Enabled = false;
        }
    }

    private bool ShouldFlip(Block suggestedBlock)
    {
        if (suggestedBlock.IsGenesis) return false;
        if (store.RecordingEnabled) return true;
        return store.ReplayEnabled && suggestedBlock.BlockAccessList is not null;
    }
}
