// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class MessageSizeEstimatorTests
    {
        [Test]
        public void Estimate_header_size()
        {
            BlockHeader header = Build.A.BlockHeader.TestObject;
            Assert.That(MessageSizeEstimator.EstimateSize(header), Is.EqualTo(512));
        }

        [Test]
        public void Estimate_null_header_size() => Assert.That(MessageSizeEstimator.EstimateSize((BlockHeader)null), Is.EqualTo(0));

        [Test]
        public void Estimate_block_size()
        {
            Block block = Build.A.Block.WithTransactions(100, MuirGlacier.Instance).TestObject;
            Assert.That(MessageSizeEstimator.EstimateSize(block), Is.EqualTo(10512));
        }

        [Test]
        public void Estimate_null_block_size() => Assert.That(MessageSizeEstimator.EstimateSize((Block)null), Is.EqualTo(0));

        [Test]
        public void Estimate_null_tx_size() => Assert.That(MessageSizeEstimator.EstimateSize((Transaction)null), Is.EqualTo(0));

        [Test]
        public void Estimate_tx_size()
        {
            Transaction tx = Build.A.Transaction.TestObject;
            Assert.That(MessageSizeEstimator.EstimateSize(tx), Is.EqualTo(100));
        }

        [Test]
        public void Estimate_tx_with_data_size()
        {
            Transaction tx = Build.A.Transaction.WithData(new byte[7]).TestObject;
            Assert.That(MessageSizeEstimator.EstimateSize(tx), Is.EqualTo(107));
        }

        [Test]
        public void Estimate_tx_receipt_size()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            Assert.That(MessageSizeEstimator.EstimateSize(txReceipt), Is.EqualTo(256 + 32));
        }
    }
}
