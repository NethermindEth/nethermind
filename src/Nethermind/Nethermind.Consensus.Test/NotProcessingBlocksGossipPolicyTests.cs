// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;
public class NotProcessingBlocksGossipPolicyTests
{
    [TestCase(true, ExpectedResult = false)]
    [TestCase(false, ExpectedResult = true)]
    public bool can_listen_gossip(bool isProcessing)
    {
        var processor = Substitute.For<IBlockchainProcessor>();
        processor.IsProcessingBlock.Returns(isProcessing);

        return ((ITxGossipPolicy)new NotProcessingBlocksGossipPolicy(processor)).ShouldListenToGossipedTransactions;
    }
}
