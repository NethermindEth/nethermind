// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Marker interface for <see cref="IBlockProcessor.IBlockTransactionsExecutor"/> used in block validation
/// and RPC. Block production use a different executor.
/// </summary>
public interface IValidationTransactionExecutor : IBlockProcessor.IBlockTransactionsExecutor
{
}
