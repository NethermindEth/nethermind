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

    private IndexTableHandler? _activeHandler;

    public IIndexTableStore Store => store;

    public IIndexTableHandler Create(ITransactionProcessor transactionProcessor)
    {
        _activeHandler = new IndexTableHandler(transactionProcessor, store, specProvider, blockTree, receiptStorage);
        return _activeHandler;
    }

    public void UpdateFinalBlockHash(Block block) => _activeHandler?.UpdateFinalBlockHash(block);

    public void RollbackBlock(Block block)
    {
        if (_activeHandler is not null)
        {
            _activeHandler.RollbackBlock(block);
        }
        else
        {
            store.Remove(0, (long)block.Number, block.Hash);
            IndexTableMergeScheduler.GetTablesForBlock((long)block.Number, (level, firstBlock, tableSize) =>
            {
                store.Remove(level, firstBlock, block.Hash);
            });
        }
    }
}
