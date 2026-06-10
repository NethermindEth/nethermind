// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Subscribe
{
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    [Parallelizable(ParallelScope.None)]
    public class TransactionReceiptsSubscriptionTests
    {
        private IJsonRpcDuplexClient _jsonRpcDuplexClient = null!;
        private IReceiptMonitor _receiptCanonicalityMonitor = null!;
        private IBlockTree _blockTree = null!;
        private ILogManager _logManager = null!;

        [SetUp]
        public void Setup()
        {
            _jsonRpcDuplexClient = Substitute.For<IJsonRpcDuplexClient>();
            _receiptCanonicalityMonitor = Substitute.For<IReceiptMonitor>();
            _blockTree = Substitute.For<IBlockTree>();
            _logManager = Substitute.For<ILogManager>();
        }

        [TearDown]
        public void TearDown()
        {
            _jsonRpcDuplexClient?.Dispose();
            _receiptCanonicalityMonitor?.Dispose();
        }

        private JsonRpcResult GetTransactionReceiptsSubscriptionResult(
            TransactionHashesFilter? filter,
            ReceiptsEventArgs receiptsEventArgs,
            out string subscriptionId,
            bool shouldReceiveResult = true)
        {
            using TransactionReceiptsSubscription subscription = new(
                _jsonRpcDuplexClient,
                _receiptCanonicalityMonitor,
                _blockTree,
                _logManager,
                filter);

            JsonRpcResult jsonRpcResult = new();
            ManualResetEvent manualResetEvent = new(false);

            subscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResult = j;
                manualResetEvent.Set();
            }));

            _receiptCanonicalityMonitor.ReceiptsInserted += Raise.EventWith(new object(), receiptsEventArgs);
            Assert.That(manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)), Is.EqualTo(shouldReceiveResult));

            subscriptionId = subscription.Id;
            return jsonRpcResult;
        }

        private List<JsonRpcResult> GetMultipleTransactionReceiptsResults(
            TransactionHashesFilter? filter,
            ReceiptsEventArgs receiptsEventArgs,
            out string subscriptionId,
            int expectedCount)
        {
            using TransactionReceiptsSubscription subscription = new(
                _jsonRpcDuplexClient,
                _receiptCanonicalityMonitor,
                _blockTree,
                _logManager,
                filter);

            List<JsonRpcResult> jsonRpcResults = [];
            using CountdownEvent received = new(Math.Max(expectedCount, 1));

            subscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResults.Add(j);
                if (!received.IsSet) received.Signal();
            }));

            _receiptCanonicalityMonitor.ReceiptsInserted += Raise.EventWith(new object(), receiptsEventArgs);

            if (expectedCount > 0)
            {
                received.Wait(TimeSpan.FromSeconds(1));
            }
            else
            {
                Thread.Sleep(200);
            }

            subscriptionId = subscription.Id;
            return jsonRpcResults;
        }

        [TestCase(200, false, TestName = "Exactly 200 hashes succeeds")]
        [TestCase(201, true, TestName = "More than 200 hashes fails")]
        public void TransactionReceiptsSubscription_hash_count_limit(int hashCount, bool shouldThrow)
        {
            HashSet<ValueHash256> hashes = Enumerable.Range(0, hashCount)
                .Select(_ => (ValueHash256)Keccak.Compute("test" + Guid.NewGuid()))
                .ToHashSet();

            TransactionHashesFilter filter = new() { TransactionHashes = hashes };

            if (shouldThrow)
            {
                Action act = () => new TransactionReceiptsSubscription(
                    _jsonRpcDuplexClient, _receiptCanonicalityMonitor, _blockTree, _logManager, filter);

                Assert.That(act, Throws.TypeOf<ArgumentException>().With.Message.Contains("Cannot subscribe to more than 200 transaction hashes"));
            }
            else
            {
                using TransactionReceiptsSubscription subscription = new(
                    _jsonRpcDuplexClient, _receiptCanonicalityMonitor, _blockTree, _logManager, filter);

                Assert.That(subscription, Is.Not.Null);
                Assert.That(subscription.Id, Does.StartWith("0x"));
            }
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_no_filter_returns_all_receipts()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            Transaction tx1 = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(null, eventArgs, out string subscriptionId, 2);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(results.Count, Is.EqualTo(2));

                string serialized1 = RpcTest.SerializeResponse(results[0].Response);
                Assert.That(serialized1, Does.Contain(subscriptionId));
                Assert.That(serialized1, Does.Contain(TestItem.KeccakA.ToString()));

                string serialized2 = RpcTest.SerializeResponse(results[1].Response);
                Assert.That(serialized2, Does.Contain(subscriptionId));
                Assert.That(serialized2, Does.Contain(TestItem.KeccakB.ToString()));
            }
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_single_hash_filter()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TransactionHashesFilter filter = new()
            {
                TransactionHashes = [TestItem.KeccakA]
            };

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            JsonRpcResult result = GetTransactionReceiptsSubscriptionResult(filter, eventArgs, out string subscriptionId);

            Assert.That(result.Response, Is.Not.Null);
            string serialized = RpcTest.SerializeResponse(result.Response);
            Assert.That(serialized, Does.Contain(subscriptionId));
            Assert.That(serialized, Does.Contain(TestItem.KeccakA.ToString()));
            Assert.That(serialized, Does.Not.Contain(TestItem.KeccakB.ToString()));
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_multiple_hashes_filter()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TransactionHashesFilter filter = new()
            {
                TransactionHashes = [TestItem.KeccakA, TestItem.KeccakC]
            };

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).TestObject;
            TxReceipt receipt3 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakC).WithIndex(2).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2, receipt3];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(filter, eventArgs, out string subscriptionId, 2);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(results.Count, Is.EqualTo(2));

                string serialized1 = RpcTest.SerializeResponse(results[0].Response);
                Assert.That(serialized1, Does.Contain(TestItem.KeccakA.ToString()));

                string serialized2 = RpcTest.SerializeResponse(results[1].Response);
                Assert.That(serialized2, Does.Contain(TestItem.KeccakC.ToString()));
            }
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_non_matching_hashes()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TransactionHashesFilter filter = new()
            {
                TransactionHashes = [TestItem.KeccakD, TestItem.KeccakE]
            };

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(filter, eventArgs, out string subscriptionId, 0);

            Assert.That(results.Count, Is.EqualTo(0));
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_partial_match()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TransactionHashesFilter filter = new()
            {
                TransactionHashes = [TestItem.KeccakA, TestItem.KeccakD]
            };

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).TestObject;
            TxReceipt receipt3 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakC).WithIndex(2).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2, receipt3];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            JsonRpcResult result = GetTransactionReceiptsSubscriptionResult(filter, eventArgs, out string subscriptionId);

            Assert.That(result.Response, Is.Not.Null);
            string serialized = RpcTest.SerializeResponse(result.Response);
            Assert.That(serialized, Does.Contain(TestItem.KeccakA.ToString()));
            Assert.That(serialized, Does.Not.Contain(TestItem.KeccakB.ToString()));
            Assert.That(serialized, Does.Not.Contain(TestItem.KeccakC.ToString()));
        }

        [Test]
        public void TransactionReceiptsSubscription_receipt_includes_all_fields()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).WithHash(TestItem.KeccakF).WithTimestamp(1000000).TestObject;

            LogEntry logEntry = Build.A.LogEntry.WithAddress(TestItem.AddressA).WithTopics(TestItem.KeccakA).TestObject;

            TxReceipt receipt = Build.A.Receipt
                .WithBlockNumber(blockNumber)
                .WithBlockHash(TestItem.KeccakF)
                .WithTransactionHash(TestItem.KeccakA)
                .WithIndex(5)
                .WithGasUsed(21000)
                .WithLogs(logEntry)
                .WithStatusCode(1)
                .TestObject;

            TxReceipt[] receipts = [receipt];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            JsonRpcResult result = GetTransactionReceiptsSubscriptionResult(null, eventArgs, out string subscriptionId);

            Assert.That(result.Response, Is.Not.Null);
            string serialized = RpcTest.SerializeResponse(result.Response);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(serialized, Does.Contain("transactionHash"));
                Assert.That(serialized, Does.Contain(TestItem.KeccakA.ToString()));
                Assert.That(serialized, Does.Contain("blockHash"));
                Assert.That(serialized, Does.Contain(TestItem.KeccakF.ToString()));
                Assert.That(serialized, Does.Contain("blockNumber"));
                Assert.That(serialized, Does.Contain("0xd903")); // 55555 in hex
                Assert.That(serialized, Does.Contain("transactionIndex"));
                Assert.That(serialized, Does.Contain("0x5")); // index 5
                Assert.That(serialized, Does.Contain("logs"));
                Assert.That(serialized, Does.Contain("status"));
                Assert.That(serialized, Does.Contain("0x1")); // status code 1
            }
        }

        [Test]
        public void TransactionReceiptsSubscription_logs_have_correct_indices()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            LogEntry log1 = Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject;
            LogEntry log2 = Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject;
            LogEntry log3 = Build.A.LogEntry.WithAddress(TestItem.AddressC).TestObject;

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).WithLogs(log1, log2).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).WithLogs(log3).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(null, eventArgs, out string subscriptionId, 2);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(results.Count, Is.EqualTo(2));

                // First receipt should have logs with indices 0 and 1
                string serialized1 = RpcTest.SerializeResponse(results[0].Response);
                Assert.That(serialized1, Does.Contain("\"logIndex\":\"0x0\""));
                Assert.That(serialized1, Does.Contain("\"logIndex\":\"0x1\""));

                // Second receipt should have log with index 2 (cumulative)
                string serialized2 = RpcTest.SerializeResponse(results[1].Response);
                Assert.That(serialized2, Does.Contain("\"logIndex\":\"0x2\""));
            }
        }

        [Test]
        public void TransactionReceiptsSubscription_empty_block_no_notification()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TxReceipt[] receipts = [];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(null, eventArgs, out string subscriptionId, 0);

            Assert.That(results.Count, Is.EqualTo(0));
        }

        [Test]
        public void TransactionReceiptsSubscription_failed_tx_still_delivered()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TxReceipt receipt = Build.A.Receipt
                .WithBlockNumber(blockNumber)
                .WithTransactionHash(TestItem.KeccakA)
                .WithIndex(0)
                .WithStatusCode(0) // Failed transaction
                .WithError("Reverted")
                .TestObject;

            TxReceipt[] receipts = [receipt];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            JsonRpcResult result = GetTransactionReceiptsSubscriptionResult(null, eventArgs, out string subscriptionId);

            Assert.That(result.Response, Is.Not.Null);
            string serialized = RpcTest.SerializeResponse(result.Response);
            Assert.That(serialized, Does.Contain(TestItem.KeccakA.ToString()));
            Assert.That(serialized, Does.Contain("0x0")); // status 0 for failed tx
        }

        [Test]
        public void TransactionReceiptsSubscription_reorg_skipped()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TxReceipt receipt = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;

            TxReceipt[] receipts = [receipt];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, wasRemoved: true); // wasRemoved=true indicates reorg

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(null, eventArgs, out string subscriptionId, 0);

            Assert.That(results.Count, Is.EqualTo(0));
        }

        [Test]
        public void TransactionReceiptsSubscription_dispose_stops_delivery()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TransactionReceiptsSubscription subscription = new(
                _jsonRpcDuplexClient,
                _receiptCanonicalityMonitor,
                _blockTree,
                _logManager,
                null);

            bool receivedEvent = false;
            subscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                receivedEvent = true;
            }));

            // Dispose the subscription
            subscription.Dispose();

            TxReceipt receipt = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt[] receipts = [receipt];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            _receiptCanonicalityMonitor.ReceiptsInserted += Raise.EventWith(new object(), eventArgs);

            Thread.Sleep(200); // Give time for any potential event delivery

            Assert.That(receivedEvent, Is.False);
        }
    }
}
