// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.IndexTables;

/// <inheritdoc cref="IIndexTableHandlerFactory"/>
public sealed class IndexTableHandlerFactory(
    IIndexTableStore store,
    ISpecProvider? specProvider = null,
    IBlockTree? blockTree = null,
    IReceiptStorage? receiptStorage = null) : IIndexTableHandlerFactory
{
    public static IndexTableHandlerFactory Default { get; } = new(new IndexTableStore());

    public IIndexTableStore Store => store;

    /// <inheritdoc />
    public IIndexTableHandler Create(ITransactionProcessor transactionProcessor) =>
        new IndexTableHandler(transactionProcessor, store, specProvider, blockTree, receiptStorage);
}
