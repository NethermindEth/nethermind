// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
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
        private readonly ISpecProvider _specProvider = new TestSpecProvider(Cancun.Instance);
        private ILogManager _logManager = null!;

        // Sends run on the subscription's background loop, and the first one can take over a second on a cold CI runner.
        private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan NoDeliveryWait = TimeSpan.FromSeconds(1);

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

        private string? GetTransactionReceiptsSubscriptionResult(
            TransactionHashesFilter? filter,
            ReceiptsEventArgs receiptsEventArgs,
            out string subscriptionId,
            bool shouldReceiveResult = true)
        {
            using TransactionReceiptsSubscription subscription = new(
                _jsonRpcDuplexClient,
                _receiptCanonicalityMonitor,
                _blockTree,
                _specProvider,
                _logManager,
                filter);

            string? serialized = null;
            ManualResetEvent manualResetEvent = new(false);

            subscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
            {
                serialized = RpcTest.SerializeResponse(j.Response);
                manualResetEvent.Set();
            }));

            _receiptCanonicalityMonitor.ReceiptsInserted += Raise.EventWith(new object(), receiptsEventArgs);
            Assert.That(manualResetEvent.WaitOne(shouldReceiveResult ? DeliveryTimeout : NoDeliveryWait), Is.EqualTo(shouldReceiveResult));

            subscriptionId = subscription.Id;
            return serialized;
        }

        /// <summary>Builds the event the monitor raises for a block, with a transaction per receipt.</summary>
        private static ReceiptsEventArgs CreateReceiptsEventArgs(BlockHeader blockHeader, TxReceipt[] receipts, bool wasRemoved = false, bool logIndexPerReceipt = false, Transaction[]? transactions = null)
        {
            transactions ??= receipts.Select(static r => Build.A.Transaction.WithHash(r.TxHash!).TestObject).ToArray();
            Block block = Build.A.Block.WithHeader(blockHeader).WithTransactions(transactions).TestObject;
            return new ReceiptsEventArgs(block, receipts, wasRemoved, logIndexPerReceipt);
        }

        private static JsonElement[] GetReceipts(string? serialized)
        {
            using JsonDocument document = JsonDocument.Parse(serialized!);
            return [.. document.RootElement.GetProperty("params").GetProperty("result").EnumerateArray().Select(static r => r.Clone())];
        }

        private static string?[] GetTransactionHashes(string? serialized) =>
            [.. GetReceipts(serialized).Select(static r => r.GetProperty("transactionHash").GetString())];

        private static TransactionHashesFilter ParseFilter(string filterJson)
        {
            using JsonDocument filterDocument = JsonDocument.Parse(filterJson);
            TransactionHashesFilter filter = new();
            filter.ReadJson(filterDocument.RootElement, EthereumJsonSerializer.JsonOptions);
            return filter;
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
                    _jsonRpcDuplexClient, _receiptCanonicalityMonitor, _blockTree, _specProvider, _logManager, filter);

                Assert.That(act, Throws.TypeOf<ArgumentException>().With.Message.Contains("Cannot subscribe to more than 200 transaction hashes"));

                string filterJson = $$"""{"transactionHashes":[{{string.Join(',', hashes.Select(static h => $"\"{h}\""))}}]}""";
                Assert.That(() => ParseFilter(filterJson), Throws.TypeOf<ArgumentException>().With.Message.Contains("Cannot subscribe to more than 200 transaction hashes"));
            }
            else
            {
                using TransactionReceiptsSubscription subscription = new(
                    _jsonRpcDuplexClient, _receiptCanonicalityMonitor, _blockTree, _specProvider, _logManager, filter);

                Assert.That(subscription, Is.Not.Null);
                Assert.That(subscription.Id, Does.StartWith("0x"));
            }
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_no_filter_returns_all_receipts([Values(null, "{}", """{"transactionHashes":[]}""")] string? filterJson)
        {
            TransactionHashesFilter? filter = filterJson is null ? null : ParseFilter(filterJson);

            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts);

            string? serialized = GetTransactionReceiptsSubscriptionResult(filter, eventArgs, out string subscriptionId);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(serialized, Does.Contain(subscriptionId));
                Assert.That(GetTransactionHashes(serialized), Is.EqualTo(new[] { TestItem.KeccakA.ToString(), TestItem.KeccakB.ToString() }));
            }
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_single_hash_filter()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TransactionHashesFilter filter = ParseFilter($$"""{"transactionHashes":["{{TestItem.KeccakA}}"]}""");

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts);

            string? serialized = GetTransactionReceiptsSubscriptionResult(filter, eventArgs, out string subscriptionId);

            Assert.That(serialized, Is.Not.Null);
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
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts);

            string? serialized = GetTransactionReceiptsSubscriptionResult(filter, eventArgs, out _);

            Assert.That(GetTransactionHashes(serialized), Is.EqualTo(new[] { TestItem.KeccakA.ToString(), TestItem.KeccakC.ToString() }));
        }

        [Test]
        public void TransactionReceiptsSubscription_on_receipts_inserted_non_matching_hashes([Values] bool nonStringHash)
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TransactionHashesFilter filter = ParseFilter(nonStringHash
                ? """{"transactionHashes":[1]}"""
                : $$"""{"transactionHashes":["{{TestItem.KeccakD}}","{{TestItem.KeccakE}}"]}""");

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts);

            string? serialized = GetTransactionReceiptsSubscriptionResult(filter, eventArgs, out _, shouldReceiveResult: false);

            Assert.That(serialized, Is.Null);
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
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts);

            string? serialized = GetTransactionReceiptsSubscriptionResult(filter, eventArgs, out string subscriptionId);

            Assert.That(serialized, Is.Not.Null);
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
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts);

            string? serialized = GetTransactionReceiptsSubscriptionResult(null, eventArgs, out string subscriptionId);

            Assert.That(serialized, Is.Not.Null);

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
        public void TransactionReceiptsSubscription_receipt_reports_gas_info([Values] bool isBlobTx)
        {
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).WithBaseFee(7).WithExcessBlobGas(0).TestObject;
            Transaction transaction = (isBlobTx
                    ? Build.A.Transaction.WithShardBlobTxTypeAndFields(blobCount: 2, isMempoolTx: false)
                    : Build.A.Transaction.WithType(TxType.EIP1559))
                .WithMaxFeePerGas(10)
                .WithMaxPriorityFeePerGas(1)
                .WithHash(TestItem.KeccakA)
                .TestObject;
            TxReceipt receipt = Build.A.Receipt.WithBlockNumber(1).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, [receipt], transactions: [transaction]);

            JsonElement result = GetReceipts(GetTransactionReceiptsSubscriptionResult(null, eventArgs, out _)).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.GetProperty("effectiveGasPrice").GetString(), Is.EqualTo("0x8"));
                Assert.That(result.TryGetProperty("blobGasUsed", out JsonElement blobGasUsed) ? blobGasUsed.GetString() : null, Is.EqualTo(isBlobTx ? "0x40000" : null));
                Assert.That(result.TryGetProperty("blobGasPrice", out JsonElement blobGasPrice) ? blobGasPrice.GetString() : null, Is.EqualTo(isBlobTx ? "0x1" : null));
            }
        }

        [Test]
        public void TransactionReceiptsSubscription_without_block_transactions_still_delivers()
        {
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;
            TxReceipt receipt = Build.A.Receipt.WithBlockNumber(1).WithTransactionHash(TestItem.KeccakA).WithIndex(0).TestObject;

            string? serialized = GetTransactionReceiptsSubscriptionResult(null, new ReceiptsEventArgs(blockHeader, [receipt]), out _);

            Assert.That(GetTransactionHashes(serialized), Is.EqualTo(new[] { TestItem.KeccakA.ToString() }));
        }

        [Test]
        public void TransactionReceiptsSubscription_logs_have_correct_indices([Values] bool logIndexPerReceipt)
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            LogEntry log1 = Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject;
            LogEntry log2 = Build.A.LogEntry.WithAddress(TestItem.AddressB).TestObject;
            LogEntry log3 = Build.A.LogEntry.WithAddress(TestItem.AddressC).TestObject;

            TxReceipt receipt1 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakA).WithIndex(0).WithLogs(log1, log2).TestObject;
            TxReceipt receipt2 = Build.A.Receipt.WithBlockNumber(blockNumber).WithTransactionHash(TestItem.KeccakB).WithIndex(1).WithLogs(log3).TestObject;

            TxReceipt[] receipts = [receipt1, receipt2];
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts, logIndexPerReceipt: logIndexPerReceipt);

            JsonElement[] results = GetReceipts(GetTransactionReceiptsSubscriptionResult(null, eventArgs, out _));
            string?[][] logIndexes = [.. results.Select(static r => r.GetProperty("logs").EnumerateArray().Select(static l => l.GetProperty("logIndex").GetString()).ToArray())];

            // Block-wide index 2, or 0 when EIP-8116 counts within the receipt
            Assert.That(logIndexes, Is.EqualTo(new[] { new[] { "0x0", "0x1" }, new[] { logIndexPerReceipt ? "0x0" : "0x2" } }));
        }

        [Test]
        public void TransactionReceiptsSubscription_empty_block_no_notification()
        {
            ulong blockNumber = 55555;
            BlockHeader blockHeader = Build.A.BlockHeader.WithNumber(blockNumber).TestObject;

            TxReceipt[] receipts = [];
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts);

            string? serialized = GetTransactionReceiptsSubscriptionResult(null, eventArgs, out _, shouldReceiveResult: false);

            Assert.That(serialized, Is.Null);
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
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts);

            string? serialized = GetTransactionReceiptsSubscriptionResult(null, eventArgs, out string subscriptionId);

            Assert.That(serialized, Is.Not.Null);
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
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts, wasRemoved: true);

            string? serialized = GetTransactionReceiptsSubscriptionResult(null, eventArgs, out _, shouldReceiveResult: false);

            Assert.That(serialized, Is.Null);
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
                _specProvider,
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
            ReceiptsEventArgs eventArgs = CreateReceiptsEventArgs(blockHeader, receipts);

            _receiptCanonicalityMonitor.ReceiptsInserted += Raise.EventWith(new object(), eventArgs);

            Thread.Sleep(200); // Give time for any potential event delivery

            Assert.That(receivedEvent, Is.False);
        }
    }
}
