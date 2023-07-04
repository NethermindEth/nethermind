// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
            var header = Build.A.BlockHeader.TestObject;
            MessageSizeEstimator.EstimateSize(header).Should().Be(512);
        }

        [Test]
        public void Estimate_null_header_size()
        {
            MessageSizeEstimator.EstimateSize((BlockHeader)null).Should().Be(0);
        }

        [Test]
        public void Estimate_block_size()
        {
            var block = Build.A.Block.WithTransactions(100, MuirGlacier.Instance).TestObject;
            MessageSizeEstimator.EstimateSize(block).Should().Be(10512);
        }

        [Test]
        public void Estimate_null_block_size()
        {
            MessageSizeEstimator.EstimateSize((Block)null).Should().Be(0);
        }

        [Test]
        public void Estimate_null_tx_size()
        {
            MessageSizeEstimator.EstimateSize((Transaction)null).Should().Be(0);
        }

        [Test]
        public void Estimate_tx_size()
        {
            Transaction tx = Build.A.Transaction.TestObject;
            MessageSizeEstimator.EstimateSize(tx).Should().Be(100);
        }

        [Test]
        public void Estimate_tx_with_data_size()
        {
            Transaction tx = Build.A.Transaction.WithData(new byte[7]).TestObject;
            MessageSizeEstimator.EstimateSize(tx).Should().Be(107);
        }

        [Test]
        public void Estimate_tx_receipt_size()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            MessageSizeEstimator.EstimateSize(txReceipt).Should().Be(256 + 32);
        }
    }
}
