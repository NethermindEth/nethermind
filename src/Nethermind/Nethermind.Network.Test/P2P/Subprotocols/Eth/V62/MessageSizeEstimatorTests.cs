// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class MessageSizeEstimatorTests
    {
        [Test]
        public void Estimate_receipts_matches_serialized_block_size()
        {
            TxReceipt[] receipts =
            [
                Build.A.Receipt.WithLogs(new LogEntry(TestItem.AddressA, [1, 2, 3], [TestItem.KeccakA])).TestObject,
                Build.A.Receipt.WithLogs(new LogEntry(TestItem.AddressB, [], [])).TestObject
            ];

            using ReceiptsMessage message = new(new ArrayPoolList<TxReceipt[]>(1) { receipts });
            RlpReader encoded = new(new ReceiptsMessageSerializer(MainnetSpecProvider.Instance).SerializeToArray(message));
            int contentLength = encoded.EnterSequence(out _).Remaining;

            // The outgoing size caps are only sound if a block's estimate covers every byte the
            // serializer writes for that block, framing included.
            Assert.That(MessageSizeEstimator.EstimateSize(receipts), Is.EqualTo((ulong)contentLength));
        }

        [Test]
        public void Estimate_eth69_receipts_matches_serialized_block_size()
        {
            TxReceipt[] receipts = SoftLimitTestHelper.CreateLogHeavyReceipts(100);

            using ReceiptsInnerMessage69 message = new(new ArrayPoolList<TxReceipt[]>(1) { receipts });
            RlpReader encoded = new(new ReceiptsMessageInnerSerializer69(MainnetSpecProvider.Instance).SerializeToArray(message));
            int contentLength = encoded.EnterSequence(out _).Remaining;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(MessageSizeEstimator.EstimateSize(receipts, new ReceiptMessageDecoder69()), Is.EqualTo((ulong)contentLength));
                Assert.That(MessageSizeEstimator.EstimateSize(receipts), Is.GreaterThan((ulong)contentLength));
            }
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

            ulong estimate = MessageSizeEstimator.EstimateSize([Build.A.Receipt.WithLogs(logs).TestObject]);
            ulong baseline = MessageSizeEstimator.EstimateSize([Build.A.Receipt.WithLogs().TestObject]);

            // Independent of the estimator internals: such a log still encodes its 20-byte address plus
            // RLP framing, so the estimate must grow with the log count — the previous "data length plus
            // topics" heuristic counted these logs as zero bytes.
            Assert.That(estimate - baseline, Is.GreaterThanOrEqualTo((ulong)(logCount * Address.Size)));
        }
    }
}
