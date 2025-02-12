// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Api;

/// <summary>
/// These classes share the same IWorldState as the main block validation. This is very important as it mean
/// it should not be used globally except under very specific case. Otherwise you might have random block processing
/// error as the IWorldState is stateful.
/// </summary>
public interface IMainProcessingContext
{
    ITransactionProcessor TransactionProcessor { get; }
    IBlockProcessor BlockProcessor { get; }
}
