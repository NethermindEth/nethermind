// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.TxPool;

namespace Nethermind.Consensus;
public class NotProcessingBlocksGossipPolicy(IBlockchainProcessor processor) : ITxGossipPolicy
{
    public bool ShouldListenToGossipedTransactions => !processor.IsProcessingBlock;
}
