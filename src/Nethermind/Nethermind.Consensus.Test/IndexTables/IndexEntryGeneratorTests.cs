// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexEntryGeneratorTests
{
    [Test]
    public void Genesis_block_produces_no_block_entry()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(0).TestObject;
        List<IndexEntry> entries = [];

        IndexEntryGenerator.GenerateEntries(header, [], [], null, entries);

        bool hasBlock = false;
        foreach (IndexEntry entry in entries)
        {
            if (entry.Type == IndexEntryType.Block)
            {
                hasBlock = true;
                break;
            }
        }
        Assert.That(hasBlock, Is.False);
    }

    [Test]
    public void Block_entry_uses_one_block_delay()
    {
        // Block N=5 should generate a block entry for block N−1=4
        Hash256 parentHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithNumber(5).TestObject;
        List<IndexEntry> entries = [];

        IndexEntryGenerator.GenerateEntries(header, [], [], parentHash, entries);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(1));
            Assert.That(entries[0].Type, Is.EqualTo(IndexEntryType.Block));
            Assert.That(entries[0].BlockNumber, Is.EqualTo(4));
        }
    }

    [Test]
    public void Single_transaction_no_logs_produces_block_and_tx_entries()
    {
        Hash256 parentHash = TestItem.KeccakA;
        Hash256 txHash = TestItem.KeccakB;
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;

        Transaction tx = Build.A.Transaction.WithHash(txHash).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithLogs().TestObject;

        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(header, [tx], [receipt], parentHash, entries);

        using (Assert.EnterMultipleScope())
        {
            // 1 block entry + 1 tx entry = 2
            Assert.That(entries.Count, Is.EqualTo(2));
            Assert.That(entries[0].Type, Is.EqualTo(IndexEntryType.Block));
            Assert.That(entries[0].BlockNumber, Is.EqualTo(9)); // parent block
            Assert.That(entries[1].Type, Is.EqualTo(IndexEntryType.Transaction));
            Assert.That(entries[1].BlockNumber, Is.EqualTo(10));
        }
    }

    [Test]
    public void Log_with_topics_produces_address_and_topic_entries()
    {
        Hash256 parentHash = TestItem.KeccakA;
        Hash256 txHash = TestItem.KeccakB;
        Hash256 topic0 = TestItem.KeccakC;
        Hash256 topic1 = TestItem.KeccakD;
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;

        LogEntry log = new(TestItem.AddressA, [], [topic0, topic1]);
        Transaction tx = Build.A.Transaction.WithHash(txHash).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithLogs(log).TestObject;

        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(header, [tx], [receipt], parentHash, entries);

        using (Assert.EnterMultipleScope())
        {
            // 1 block + 1 tx + 1 address + 2 topics = 5
            Assert.That(entries.Count, Is.EqualTo(5));
            Assert.That(entries[0].Type, Is.EqualTo(IndexEntryType.Block));
            Assert.That(entries[1].Type, Is.EqualTo(IndexEntryType.Transaction));
            Assert.That(entries[2].Type, Is.EqualTo(IndexEntryType.LogAddress));
            Assert.That(entries[3].Type, Is.EqualTo(IndexEntryType.LogTopic0));
            Assert.That(entries[4].Type, Is.EqualTo(IndexEntryType.LogTopic1));
        }
    }

    [Test]
    public void Cumulative_log_count_tracks_correctly_across_transactions()
    {
        Hash256 parentHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;

        (Transaction[] txs, TxReceipt[] receipts) = CreateTwoTransactionsWithLogs();

        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(header, txs, receipts, parentHash, entries);

        List<IndexEntry> txEntries = [];
        foreach (IndexEntry e in entries)
        {
            if (e.Type == IndexEntryType.Transaction)
            {
                txEntries.Add(e);
            }
        }

        Assert.That(txEntries.Count, Is.EqualTo(2));

        // Encode tx0 and check cumulative log count (last 4 bytes)
        byte[] buf0 = new byte[txEntries[0].EncodedLength];
        txEntries[0].Encode(buf0);
        uint cumulativeLogCount0 = (uint)(buf0[46] << 24 | buf0[47] << 16 | buf0[48] << 8 | buf0[49]);

        // Encode tx1 and check cumulative log count
        byte[] buf1 = new byte[txEntries[1].EncodedLength];
        txEntries[1].Encode(buf1);
        uint cumulativeLogCount1 = (uint)(buf1[46] << 24 | buf1[47] << 16 | buf1[48] << 8 | buf1[49]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cumulativeLogCount0, Is.EqualTo(0u));
            Assert.That(cumulativeLogCount1, Is.EqualTo(2u));
        }
    }

    [Test]
    public void Log_index_is_relative_to_transaction()
    {
        Hash256 parentHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;

        (Transaction[] txs, TxReceipt[] receipts) = CreateTwoTransactionsWithLogs();

        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(header, txs, receipts, parentHash, entries);

        List<IndexEntry> logAddressEntries = [];
        foreach (IndexEntry e in entries)
        {
            if (e.Type == IndexEntryType.LogAddress)
            {
                logAddressEntries.Add(e);
            }
        }

        Assert.That(logAddressEntries.Count, Is.EqualTo(3));

        // First tx, first log: logIndex=0
        byte[] buf0 = new byte[logAddressEntries[0].EncodedLength];
        logAddressEntries[0].Encode(buf0);
        uint logIndex0 = (uint)(buf0[34] << 24 | buf0[35] << 16 | buf0[36] << 8 | buf0[37]);

        // First tx, second log: logIndex=1
        byte[] buf1 = new byte[logAddressEntries[1].EncodedLength];
        logAddressEntries[1].Encode(buf1);
        uint logIndex1 = (uint)(buf1[34] << 24 | buf1[35] << 16 | buf1[36] << 8 | buf1[37]);

        // Second tx, first log: logIndex=0 (reset per transaction)
        byte[] buf2 = new byte[logAddressEntries[2].EncodedLength];
        logAddressEntries[2].Encode(buf2);
        uint logIndex2 = (uint)(buf2[34] << 24 | buf2[35] << 16 | buf2[36] << 8 | buf2[37]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logIndex0, Is.EqualTo(0u));
            Assert.That(logIndex1, Is.EqualTo(1u));
            Assert.That(logIndex2, Is.EqualTo(0u));
        }
    }

    [Test]
    public void Four_topics_produce_four_topic_entries()
    {
        Hash256 parentHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;

        LogEntry log = new(TestItem.AddressA, [],
            [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD]);

        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakE).TestObject;
        TxReceipt receipt = Build.A.Receipt.WithLogs(log).TestObject;

        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(header, [tx], [receipt], parentHash, entries);

        List<IndexEntry> topicEntries = [];
        foreach (IndexEntry e in entries)
        {
            if (e.Type is >= IndexEntryType.LogTopic0 and <= IndexEntryType.LogTopic3)
            {
                topicEntries.Add(e);
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(topicEntries.Count, Is.EqualTo(4));
            Assert.That(topicEntries[0].Type, Is.EqualTo(IndexEntryType.LogTopic0));
            Assert.That(topicEntries[1].Type, Is.EqualTo(IndexEntryType.LogTopic1));
            Assert.That(topicEntries[2].Type, Is.EqualTo(IndexEntryType.LogTopic2));
            Assert.That(topicEntries[3].Type, Is.EqualTo(IndexEntryType.LogTopic3));
        }
    }

    [Test]
    public void Transaction_entry_is_generated_when_the_hash_is_not_memoized()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
        Transaction tx = new() { To = TestItem.AddressA, GasLimit = 21000 };
        Assert.That(tx.Hash, Is.Null, "precondition: the hash must not be memoized");

        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(header, [tx], [Build.A.Receipt.WithLogs().TestObject], TestItem.KeccakA, entries);

        bool hasTx = false;
        foreach (IndexEntry e in entries)
        {
            if (e.Type == IndexEntryType.Transaction)
            {
                hasTx = true;
                break;
            }
        }
        Assert.That(hasTx, Is.True);
    }

    [Test]
    public void Log_topics_beyond_the_four_indexable_ones_are_ignored()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(42).TestObject;
        LogEntry log = new(
            TestItem.AddressA,
            [],
            [TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD, TestItem.KeccakE, TestItem.KeccakF]);
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;

        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(header, [tx], [Build.A.Receipt.WithLogs(log).TestObject], TestItem.KeccakA, entries);

        int topicCount = 0;
        foreach (IndexEntry e in entries)
        {
            if (e.Type is >= IndexEntryType.LogTopic0 and <= IndexEntryType.LogTopic3)
            {
                topicCount++;
            }
        }
        Assert.That(topicCount, Is.EqualTo(4));
    }

    private static (Transaction[] Txs, TxReceipt[] Receipts) CreateTwoTransactionsWithLogs()
    {
        LogEntry log0a = new(TestItem.AddressA, [], [TestItem.KeccakC]);
        LogEntry log0b = new(TestItem.AddressB, [], [TestItem.KeccakD]);
        LogEntry log1a = new(TestItem.AddressC, [], [TestItem.KeccakE]);

        Transaction tx0 = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;
        Transaction tx1 = Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject;

        TxReceipt receipt0 = Build.A.Receipt.WithLogs(log0a, log0b).TestObject;
        TxReceipt receipt1 = Build.A.Receipt.WithLogs(log1a).TestObject;

        return ([tx0, tx1], [receipt0, receipt1]);
    }

    [TestCase(1, 0)]
    [TestCase(0, 1)]
    [TestCase(2, 1)]
    public void Receipts_not_parallel_with_transactions_are_rejected(int txCount, int receiptCount)
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
        Transaction[] transactions = new Transaction[txCount];
        for (int i = 0; i < txCount; i++)
        {
            transactions[i] = Build.A.Transaction.WithHash(TestItem.Keccaks[i]).TestObject;
        }

        TxReceipt[] receipts = new TxReceipt[receiptCount];
        for (int i = 0; i < receiptCount; i++)
        {
            receipts[i] = Build.A.Receipt.WithLogs().TestObject;
        }

        Assert.Throws<ArgumentException>(() =>
            IndexEntryGenerator.GenerateEntries(header, transactions, receipts, TestItem.KeccakA, []));
    }

    [Test]
    public void Empty_block_with_no_transactions_produces_only_block_entry()
    {
        Hash256 parentHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader.WithNumber(100).TestObject;

        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(header, [], [], parentHash, entries);

        Assert.That(entries.Count, Is.EqualTo(1));
        Assert.That(entries[0].Type, Is.EqualTo(IndexEntryType.Block));
        Assert.That(entries[0].BlockNumber, Is.EqualTo(99));
    }

    [Test]
    public void Non_genesis_block_without_parent_hash_throws_argument_exception()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
        header.ParentHash = null!;

        Assert.Throws<ArgumentException>(() =>
            IndexEntryGenerator.GenerateEntries(header, [], [], null, []));
    }

    [Test]
    public void Transaction_hash_is_memoized_after_entry_generation()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(10).TestObject;
        Transaction tx = new() { To = TestItem.AddressA, GasLimit = 21000 };
        Assert.That(tx.Hash, Is.Null);

        IndexEntryGenerator.GenerateEntries(header, [tx], [Build.A.Receipt.WithLogs().TestObject], TestItem.KeccakA, []);

        Assert.That(tx.Hash, Is.Not.Null);
    }
}
