// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Creates an <see cref="IIndexTableHandler"/> for use with a specific <see cref="ITransactionProcessor"/>,
/// allowing system contract execution to target the post-execution processor in Block Access List management.
/// </summary>
public interface IIndexTableHandlerFactory
{
    /// <summary>
    /// Gets the underlying index table store used by handlers created by this factory.
    /// </summary>
    IIndexTableStore Store { get; }

    /// <summary>
    /// Creates a new <see cref="IIndexTableHandler"/> configured with the given transaction processor.
    /// </summary>
    /// <param name="transactionProcessor">The transaction processor to use for system contract execution.</param>
    /// <returns>A new <see cref="IIndexTableHandler"/> instance.</returns>
    IIndexTableHandler Create(ITransactionProcessor transactionProcessor);
}

