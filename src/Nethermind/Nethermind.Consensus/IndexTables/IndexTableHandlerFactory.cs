// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;

namespace Nethermind.Consensus.IndexTables;

/// <inheritdoc cref="IIndexTableHandlerFactory"/>
public sealed class IndexTableHandlerFactory(
    IIndexTableStore store,
    ISpecProvider specProvider,
    IBlockTree? blockTree = null,
    IReceiptStorage? receiptStorage = null,
    ILogManager? logManager = null) : IIndexTableHandlerFactory
{
    public IIndexTableStore Store => store;

    /// <inheritdoc />
    public IIndexTableHandler Create(ITransactionProcessor transactionProcessor) =>
        new IndexTableHandler(transactionProcessor, store, specProvider, null, blockTree, receiptStorage, logManager);

    /// <inheritdoc />
    public IIndexTableHandler Create(ITransactionProcessor transactionProcessor, IWorldState? worldState) =>
        new IndexTableHandler(transactionProcessor, store, specProvider, worldState, blockTree, receiptStorage, logManager);
}
