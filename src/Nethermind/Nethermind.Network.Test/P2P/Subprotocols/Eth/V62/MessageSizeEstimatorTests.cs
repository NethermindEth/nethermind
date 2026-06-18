// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Serialization.Rlp;
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
        public void Estimate_block_size_aggregates_header_and_transactions()
        {
            Block block = Build.A.Block.WithTransactions(100, MuirGlacier.Instance).TestObject;

            ulong expected = MessageSizeEstimator.EstimateSize(block.Header);
            foreach (Transaction tx in block.Transactions)
            {
                expected += MessageSizeEstimator.EstimateSize(tx);
            }

            Assert.That(MessageSizeEstimator.EstimateSize(block), Is.EqualTo(expected));
        }

        [Test]
        public void Estimate_null_block_size() => Assert.That(MessageSizeEstimator.EstimateSize((Block)null), Is.EqualTo(0));

        [Test]
        public void Estimate_null_tx_size() => Assert.That(MessageSizeEstimator.EstimateSize((Transaction)null), Is.EqualTo(0));

        [Test]
        public void Estimate_tx_size_matches_encoded_length()
        {
            Transaction tx = Build.A.Transaction.TestObject;
            Assert.That(MessageSizeEstimator.EstimateSize(tx), Is.EqualTo((ulong)TxDecoder.Instance.GetLength(tx, RlpBehaviors.None)));
        }

        [Test]
        public void Estimate_tx_with_data_size_matches_encoded_length()
        {
            Transaction tx = Build.A.Transaction.WithData(new byte[7]).TestObject;
            Assert.That(MessageSizeEstimator.EstimateSize(tx), Is.EqualTo((ulong)TxDecoder.Instance.GetLength(tx, RlpBehaviors.None)));
        }

        [Test]
        public void Estimate_tx_counts_access_list()
        {
            AccessList.Builder builder = new();
            builder.AddAddress(TestItem.AddressA);
            for (int i = 0; i < 1000; i++)
            {
                builder.AddStorage(new UInt256((ulong)i));
            }

            Transaction tx = Build.A.Transaction.WithType(TxType.AccessList).WithAccessList(builder.Build()).TestObject;

            ulong estimate = MessageSizeEstimator.EstimateSize(tx);

            // The previous "100 + data length" heuristic ignored the access list and would massively
            // under-count this transaction; the encoded length includes it.
            Assert.That(estimate, Is.EqualTo((ulong)TxDecoder.Instance.GetLength(tx, RlpBehaviors.None)));
            Assert.That(estimate, Is.GreaterThan(1000UL * Hash256.Size));
        }

        [Test]
        public void Estimate_tx_receipt_size()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            Assert.That(MessageSizeEstimator.EstimateSize(txReceipt), Is.EqualTo(256 + 32));
        }
    }
}
