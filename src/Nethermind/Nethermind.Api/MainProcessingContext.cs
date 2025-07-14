// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Api;

public record MainProcessingContext(
    ITransactionProcessor TransactionProcessor,
    IBranchProcessor BranchProcessor,
    IBlockProcessor BlockProcessor,
    IBlockchainProcessor BlockchainProcessor,
    IWorldState WorldState
) : IMainProcessingContext
{
}
