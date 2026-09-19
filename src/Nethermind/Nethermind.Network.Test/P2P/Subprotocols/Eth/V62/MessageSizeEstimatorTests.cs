// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
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
            const int dataLength = 7;
            Transaction baseline = Build.A.Transaction.TestObject;
            Transaction tx = Build.A.Transaction.WithData(new byte[dataLength]).TestObject;

            ulong estimate = MessageSizeEstimator.EstimateSize(tx);

            Assert.That(estimate, Is.EqualTo((ulong)TxDecoder.Instance.GetLength(tx, RlpBehaviors.None)));
            // Independent of the estimator internals: dataLength extra calldata bytes encode to
            // dataLength extra bytes, so the estimate must grow by exactly that — guarding against a
            // regression to a constant or to a heuristic that drops the field.
            Assert.That(estimate - MessageSizeEstimator.EstimateSize(baseline), Is.EqualTo((ulong)dataLength));
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
        public void Estimate_receipts_matches_serialized_block_size()
        {
            TxReceipt[] receipts =
            [
                Build.A.Receipt.WithLogs(new LogEntry(TestItem.AddressA, [1, 2, 3], [TestItem.KeccakA])).TestObject,
                Build.A.Receipt.WithLogs(new LogEntry(TestItem.AddressB, [], [])).TestObject
            ];

            using ReceiptsMessage message = new(new ArrayPoolList<TxReceipt[]>(1) { receipts });
            new ReceiptsMessageSerializer(MainnetSpecProvider.Instance).GetLength(message, out int contentLength);

            // The outgoing size caps are only sound if a block's estimate covers every byte the
            // serializer writes for that block, framing included.
            Assert.That(MessageSizeEstimator.EstimateSize(receipts), Is.EqualTo((ulong)contentLength));
        }

        [Test]
        public void Estimate_tx_receipt_counts_logs_without_topics_or_data()
        {
            const int logCount = 100;
            LogEntry[] logs = new LogEntry[logCount];
            for (int i = 0; i < logCount; i++)
            {
                logs[i] = new LogEntry(TestItem.AddressA, [], []);
            }

            ulong estimate = MessageSizeEstimator.EstimateSize(Build.A.Receipt.WithLogs(logs).TestObject);
            ulong baseline = MessageSizeEstimator.EstimateSize(Build.A.Receipt.WithLogs().TestObject);

            // Independent of the estimator internals: such a log still encodes its 20-byte address plus
            // RLP framing, so the estimate must grow with the log count — the previous "data length plus
            // topics" heuristic counted these logs as zero bytes.
            Assert.That(estimate - baseline, Is.GreaterThanOrEqualTo((ulong)(logCount * Address.Size)));
        }
    }
}
