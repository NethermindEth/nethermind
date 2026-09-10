// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Creates an <see cref="IIndexTableHandler"/> for use with a specific <see cref="ITransactionProcessor"/>,
/// allowing system contract execution to target the post-execution processor in Block Access List management.
/// </summary>
public interface IIndexTableHandlerFactory
{
    IIndexTableHandler Create(ITransactionProcessor transactionProcessor);
    void RollbackBlock(Block block);
    void UpdateFinalBlockHash(Block block);
}
