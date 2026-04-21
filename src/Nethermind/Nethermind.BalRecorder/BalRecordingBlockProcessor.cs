// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.BalRecorder;

public class BalRecordingBlockProcessor(
    IBlockProcessor inner,
    IRecordedBalStore store,
    IWorldState stateProvider) : IBlockProcessor
{
    private readonly IBlockAccessListBuilder? _balBuilder = stateProvider as IBlockAccessListBuilder;

    public event Action? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token)
    {
        (Block block, TxReceipt[] receipts) = inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

        if (store.RecordingEnabled && _balBuilder is not null)
            store.Insert(block, _balBuilder.GeneratedBlockAccessList);

        return (block, receipts);
    }
}
