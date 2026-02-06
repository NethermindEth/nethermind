// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Test.Filters;

/// <summary>
/// Test implementation of IBranchProcessor that allows manual event raising.
/// </summary>
internal class TestBranchProcessor : IBranchProcessor
{
    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;
    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing { add { } remove { } }
    public event EventHandler<BlockEventArgs>? BlockProcessing { add { } remove { } }

    public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks,
        ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token = default)
        => [];

    public void RaiseBlockProcessed(BlockProcessedEventArgs args)
        => BlockProcessed?.Invoke(this, args);
}

/// <summary>
/// Test implementation of IMainProcessingContext that allows manual event raising.
/// </summary>
internal class TestMainProcessingContext : IMainProcessingContext
{
    private readonly TestBranchProcessor _branchProcessor = new();

    public ITransactionProcessor TransactionProcessor => null!;
    public IBranchProcessor BranchProcessor => _branchProcessor;
    public IBlockProcessor BlockProcessor => null!;
    public IBlockchainProcessor BlockchainProcessor => null!;
    public IBlockProcessingQueue BlockProcessingQueue => null!;
    public IWorldState WorldState => null!;
    public IGenesisLoader GenesisLoader => null!;

    public event EventHandler<TxProcessedEventArgs>? TransactionProcessed;

    public TestBranchProcessor TestBranchProcessor => _branchProcessor;

    public void RaiseTransactionProcessed(TxProcessedEventArgs args)
        => TransactionProcessed?.Invoke(this, args);
}
