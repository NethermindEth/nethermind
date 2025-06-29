// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Api;

public record MainProcessingContext(
    ITransactionProcessor TransactionProcessor,
    IBlockProcessor BlockProcessor,
    IBlockchainProcessor BlockchainProcessor,
    IVisitingWorldState WorldState
) : IMainProcessingContext
{
}
