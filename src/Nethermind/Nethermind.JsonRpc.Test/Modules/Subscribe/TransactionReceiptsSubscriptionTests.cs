// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Subscribe
{
    [Parallelizable(ParallelScope.None)]
    public class TransactionReceiptsSubscriptionTests
    {
        private IJsonRpcDuplexClient _jsonRpcDuplexClient = null!;
        private IReceiptMonitor _receiptCanonicalityMonitor = null!;
        private IBlockTree _blockTree = null!;
        private ILogManager _logManager = null!;
        private IJsonSerializer _jsonSerializer = null!;

        [SetUp]
        public void Setup()
        {
            _jsonRpcDuplexClient = Substitute.For<IJsonRpcDuplexClient>();
            _receiptCanonicalityMonitor = Substitute.For<IReceiptMonitor>();
            _blockTree = Substitute.For<IBlockTree>();
            _logManager = Substitute.For<ILogManager>();
            _jsonSerializer = new EthereumJsonSerializer();
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
            TransactionReceiptsSubscription subscription = new(
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
            manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);

            subscriptionId = subscription.Id;
            return jsonRpcResult;
        }

        private List<JsonRpcResult> GetMultipleTransactionReceiptsResults(
            TransactionHashesFilter? filter,
            ReceiptsEventArgs receiptsEventArgs,
            out string subscriptionId,
            int expectedCount)
        {
            TransactionReceiptsSubscription subscription = new(
                _jsonRpcDuplexClient,
                _receiptCanonicalityMonitor,
                _blockTree,
                _logManager,
                filter);

            List<JsonRpcResult> jsonRpcResults = new();
            SemaphoreSlim semaphoreSlim = new(0, 1);

            subscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                jsonRpcResults.Add(j);
            }));

            _receiptCanonicalityMonitor.ReceiptsInserted += Raise.EventWith(new object(), receiptsEventArgs);
            semaphoreSlim.Wait(TimeSpan.FromMilliseconds(500));

            subscriptionId = subscription.Id;
            return jsonRpcResults;
        }

        [Test]
        public void TransactionReceiptsSubscription_with_too_many_hashes_fails()
        {
            // Create more than 200 hashes
            Hash256[] tooManyHashes = Enumerable.Range(0, 201)
                .Select(_ => Keccak.Compute("test" + Guid.NewGuid()))
                .ToArray();

            TransactionHashesFilter filter = new()
            {
                TransactionHashes = tooManyHashes
            };

            Action act = () => new TransactionReceiptsSubscription(
                _jsonRpcDuplexClient,
                _receiptCanonicalityMonitor,
                _blockTree,
                _logManager,
                filter);

            act.Should().Throw<ArgumentException>()
                .WithMessage("*cannot subscribe to more than 200 transaction hashes*");
        }

        [Test]
        public void TransactionReceiptsSubscription_with_exactly_200_hashes_succeeds()
        {
            // Create exactly 200 hashes (boundary test for the limit)
            Hash256[] exactly200Hashes = Enumerable.Range(0, 200)
                .Select(_ => Keccak.Compute("test" + Guid.NewGuid()))
                .ToArray();

            TransactionHashesFilter filter = new()
            {
                TransactionHashes = exactly200Hashes
            };

            TransactionReceiptsSubscription subscription = new(
                _jsonRpcDuplexClient,
                _receiptCanonicalityMonitor,
                _blockTree,
                _logManager,
                filter);

            subscription.Should().NotBeNull();
            subscription.Id.Should().StartWith("0x");
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_no_filter_returns_all_receipts()
        {
            int blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            Transaction tx1 = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(null, eventArgs, out string subscriptionId, 2);

            results.Count.Should().Be(2);

            string serialized1 = _jsonSerializer.Serialize(results[0].Response);
            serialized1.Should().Contain(subscriptionId);
            serialized1.Should().Contain(TestItem.KeccakA.ToString());

            string serialized2 = _jsonSerializer.Serialize(results[1].Response);
            serialized2.Should().Contain(subscriptionId);
            serialized2.Should().Contain(TestItem.KeccakB.ToString());
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_single_hash_filter()
        {
            int blockNumber = 55555;
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

            result.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(result.Response);
            serialized.Should().Contain(subscriptionId);
            serialized.Should().Contain(TestItem.KeccakA.ToString());
            serialized.Should().NotContain(TestItem.KeccakB.ToString());
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_multiple_hashes_filter()
        {
            int blockNumber = 55555;
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

            results.Count.Should().Be(2);

            string serialized1 = _jsonSerializer.Serialize(results[0].Response);
            serialized1.Should().Contain(TestItem.KeccakA.ToString());

            string serialized2 = _jsonSerializer.Serialize(results[1].Response);
            serialized2.Should().Contain(TestItem.KeccakC.ToString());
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_non_matching_hashes()
        {
            int blockNumber = 55555;
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

            results.Count.Should().Be(0);
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_partial_match()
        {
            int blockNumber = 55555;
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

            result.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(result.Response);
            serialized.Should().Contain(TestItem.KeccakA.ToString());
            serialized.Should().NotContain(TestItem.KeccakB.ToString());
            serialized.Should().NotContain(TestItem.KeccakC.ToString());
        }

        [Test]
        public void TransactionReceiptsSubscription_receipt_includes_all_fields()
        {
            int blockNumber = 55555;
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

            result.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(result.Response);

            serialized.Should().Contain("transactionHash");
            serialized.Should().Contain(TestItem.KeccakA.ToString());
            serialized.Should().Contain("blockHash");
            serialized.Should().Contain(TestItem.KeccakF.ToString());
            serialized.Should().Contain("blockNumber");
            serialized.Should().Contain("0xd903"); // 55555 in hex
            serialized.Should().Contain("transactionIndex");
            serialized.Should().Contain("0x5"); // index 5
            serialized.Should().Contain("logs");
            serialized.Should().Contain("status");
            serialized.Should().Contain("0x1"); // status code 1
        }

        [Test]
        public void TransactionReceiptsSubscription_logs_have_correct_indices()
        {
            int blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            LogEntry log1 = Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject;
            LogEntry log2 = Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject;
            LogEntry log3 = Build.A.LogEntry.WithAddress(TestItem.AddressC).TestObject;

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).WithLogs(log1, log2).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).WithLogs(log3).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(null, eventArgs, out string subscriptionId, 2);

            results.Count.Should().Be(2);

            // First receipt should have logs with indices 0 and 1
            string serialized1 = _jsonSerializer.Serialize(results[0].Response);
            serialized1.Should().Contain("\"logIndex\":\"0x0\"");
            serialized1.Should().Contain("\"logIndex\":\"0x1\"");

            // Second receipt should have log with index 2 (cumulative)
            string serialized2 = _jsonSerializer.Serialize(results[1].Response);
            serialized2.Should().Contain("\"logIndex\":\"0x2\"");
        }

        [Test]
        public void TransactionReceiptsSubscription_empty_block_no_notification()
        {
            int blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TxReceipt[] receipts = [];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, false);

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(null, eventArgs, out string subscriptionId, 0);

            results.Count.Should().Be(0);
        }

        [Test]
        public void TransactionReceiptsSubscription_failed_tx_still_delivered()
        {
            int blockNumber = 55555;
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

            result.Response.Should().NotBeNull();
            string serialized = _jsonSerializer.Serialize(result.Response);
            serialized.Should().Contain(TestItem.KeccakA.ToString());
            serialized.Should().Contain("0x0"); // status 0 for failed tx
        }

        [Test]
        public void TransactionReceiptsSubscription_reorg_skipped()
        {
            int blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TxReceipt receipt = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;

            TxReceipt[] receipts = [receipt];
            ReceiptsEventArgs eventArgs = new(blockHeader, receipts, wasRemoved: true); // wasRemoved=true indicates reorg

            List<JsonRpcResult> results = GetMultipleTransactionReceiptsResults(null, eventArgs, out string subscriptionId, 0);

            results.Count.Should().Be(0);
        }

        [Test]
        public void TransactionReceiptsSubscription_dispose_stops_delivery()
        {
            int blockNumber = 55555;
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

            receivedEvent.Should().BeFalse();
        }
    }
}
