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

public class IndexEntryTests
{
    [TestCase(IndexEntryType.Block, 42)]
    [TestCase(IndexEntryType.Transaction, 50)]
    [TestCase(IndexEntryType.LogAddress, 38)]
    [TestCase(IndexEntryType.LogTopic0, 50)]
    [TestCase(IndexEntryType.LogTopic1, 50)]
    [TestCase(IndexEntryType.LogTopic2, 50)]
    [TestCase(IndexEntryType.LogTopic3, 50)]
    public void EncodedLength_matches_eip8304_spec(IndexEntryType type, int expectedLength)
    {
        IndexEntry entry = type switch
        {
            IndexEntryType.Block => IndexEntry.CreateBlock(TestItem.KeccakA, 1),
            IndexEntryType.Transaction => IndexEntry.CreateTransaction(TestItem.KeccakA, 1, 0, 0),
            IndexEntryType.LogAddress => IndexEntry.CreateLogAddress(TestItem.AddressA, 1, 0, 0),
            _ => IndexEntry.CreateLogTopic((int)type - 3, TestItem.KeccakA, 1, 0, 0),
        };

        Assert.That(entry.EncodedLength, Is.EqualTo(expectedLength));
    }

    [Test]
    public void Block_entry_encodes_correctly()
    {
        Hash256 blockHash = TestItem.KeccakA;
        ulong blockNumber = 40;

        IndexEntry entry = IndexEntry.CreateBlock(blockHash, blockNumber);

        Span<byte> buffer = stackalloc byte[entry.EncodedLength];
        int written = entry.Encode(buffer);

        Assert.That(written, Is.EqualTo(42));

        // Type ID = 0x0000
        Assert.That(buffer[0], Is.EqualTo(0x00));
        Assert.That(buffer[1], Is.EqualTo(0x00));

        // Content = block hash (32 bytes)
        Assert.That(buffer.Slice(2, 32).SequenceEqual(blockHash.Bytes), Is.True);

        // Block number = 40 (big-endian, 8 bytes)
        Assert.That(buffer[34], Is.EqualTo(0x00));
        Assert.That(buffer[35], Is.EqualTo(0x00));
        Assert.That(buffer[36], Is.EqualTo(0x00));
        Assert.That(buffer[37], Is.EqualTo(0x00));
        Assert.That(buffer[38], Is.EqualTo(0x00));
        Assert.That(buffer[39], Is.EqualTo(0x00));
        Assert.That(buffer[40], Is.EqualTo(0x00));
        Assert.That(buffer[41], Is.EqualTo(40));
    }

    [Test]
    public void Transaction_entry_encodes_correctly()
    {
        Hash256 txHash = TestItem.KeccakB;
        ulong blockNumber = 43;
        uint txIndex = 1;
        uint cumulativeLogCount = 2;

        IndexEntry entry = IndexEntry.CreateTransaction(txHash, blockNumber, txIndex, cumulativeLogCount);

        Span<byte> buffer = stackalloc byte[entry.EncodedLength];
        int written = entry.Encode(buffer);

        Assert.That(written, Is.EqualTo(50));

        // Type ID = 0x0001
        Assert.That(buffer[0], Is.EqualTo(0x00));
        Assert.That(buffer[1], Is.EqualTo(0x01));

        // Content = tx hash (32 bytes)
        Assert.That(buffer.Slice(2, 32).SequenceEqual(txHash.Bytes), Is.True);

        // Block number = 43 (big-endian)
        Assert.That(buffer[41], Is.EqualTo(43));

        // Tx index = 1 (big-endian, 4 bytes)
        Assert.That(buffer[42], Is.EqualTo(0x00));
        Assert.That(buffer[43], Is.EqualTo(0x00));
        Assert.That(buffer[44], Is.EqualTo(0x00));
        Assert.That(buffer[45], Is.EqualTo(0x01));

        // Cumulative log count = 2 (big-endian, 4 bytes)
        Assert.That(buffer[46], Is.EqualTo(0x00));
        Assert.That(buffer[47], Is.EqualTo(0x00));
        Assert.That(buffer[48], Is.EqualTo(0x00));
        Assert.That(buffer[49], Is.EqualTo(0x02));
    }

    [Test]
    public void LogAddress_entry_encodes_correctly()
    {
        Address address = TestItem.AddressA;
        ulong blockNumber = 42;
        uint txIndex = 0;
        uint logIndex = 1;

        IndexEntry entry = IndexEntry.CreateLogAddress(address, blockNumber, txIndex, logIndex);

        Span<byte> buffer = stackalloc byte[entry.EncodedLength];
        int written = entry.Encode(buffer);

        Assert.That(written, Is.EqualTo(38));

        // Type ID = 0x0002
        Assert.That(buffer[0], Is.EqualTo(0x00));
        Assert.That(buffer[1], Is.EqualTo(0x02));

        // Content = address (20 bytes)
        Assert.That(buffer.Slice(2, 20).SequenceEqual(address.Bytes), Is.True);

        // Block number (big-endian)
        Assert.That(buffer[29], Is.EqualTo(42));

        // Tx index = 0
        Assert.That(buffer[30], Is.EqualTo(0x00));
        Assert.That(buffer[31], Is.EqualTo(0x00));
        Assert.That(buffer[32], Is.EqualTo(0x00));
        Assert.That(buffer[33], Is.EqualTo(0x00));

        // Log index = 1
        Assert.That(buffer[34], Is.EqualTo(0x00));
        Assert.That(buffer[35], Is.EqualTo(0x00));
        Assert.That(buffer[36], Is.EqualTo(0x00));
        Assert.That(buffer[37], Is.EqualTo(0x01));
    }

    [Test]
    public void LogTopic_entry_encodes_correctly()
    {
        Hash256 topic = TestItem.KeccakC;
        ulong blockNumber = 42;
        uint txIndex = 0;
        uint logIndex = 0;

        IndexEntry entry = IndexEntry.CreateLogTopic(2, topic, blockNumber, txIndex, logIndex);

        Span<byte> buffer = stackalloc byte[entry.EncodedLength];
        int written = entry.Encode(buffer);

        Assert.That(written, Is.EqualTo(50));

        // Type ID = 0x0005 (LogTopic2 = 3 + 2 = 5)
        Assert.That(buffer[0], Is.EqualTo(0x00));
        Assert.That(buffer[1], Is.EqualTo(0x05));

        // Content = topic (32 bytes)
        Assert.That(buffer.Slice(2, 32).SequenceEqual(topic.Bytes), Is.True);
    }

    [TestCase(-1)]
    [TestCase(4)]
    [TestCase(5)]
    public void CreateLogTopic_rejects_invalid_topic_index(int topicIndex) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IndexEntry.CreateLogTopic(topicIndex, TestItem.KeccakA, 1, 0, 0));

    [Test]
    public void Lexicographic_sort_orders_by_type_then_content_then_position()
    {
        // Same type, different content — content ordering should dominate
        IndexEntry a = IndexEntry.CreateBlock(TestItem.KeccakA, 1);
        IndexEntry b = IndexEntry.CreateBlock(TestItem.KeccakB, 1);

        int cmp = a.CompareTo(b);
        // KeccakA vs KeccakB — consistent ordering based on hash values
        Assert.That(cmp, Is.Not.EqualTo(0));

        // Different types — lower type ID sorts first
        IndexEntry blockEntry = IndexEntry.CreateBlock(TestItem.KeccakA, 1);
        IndexEntry txEntry = IndexEntry.CreateTransaction(TestItem.KeccakA, 1, 0, 0);
        Assert.That(blockEntry.CompareTo(txEntry), Is.LessThan(0));

        // Same type and content, different block numbers
        IndexEntry earlier = IndexEntry.CreateTransaction(TestItem.KeccakA, 10, 0, 0);
        IndexEntry later = IndexEntry.CreateTransaction(TestItem.KeccakA, 20, 0, 0);
        Assert.That(earlier.CompareTo(later), Is.LessThan(0));
    }

    [Test]
    public void CompareTo_matches_binary_encoding_lexicographic_order()
    {
        IndexEntry[] entries =
        [
            IndexEntry.CreateBlock(TestItem.KeccakA, 10),
            IndexEntry.CreateTransaction(TestItem.KeccakB, 10, 0, 0),
            IndexEntry.CreateLogAddress(TestItem.AddressA, 10, 0, 0),
            IndexEntry.CreateLogTopic(0, TestItem.KeccakC, 10, 0, 0),
        ];

        // Sort by CompareTo
        List<IndexEntry> sortedByCompare = [.. entries];
        sortedByCompare.Sort();

        // Sort by binary encoding
        List<(byte[] encoded, IndexEntry entry)> sortedByBytes = [];
        foreach (IndexEntry entry in entries)
        {
            byte[] buf = new byte[entry.EncodedLength];
            entry.Encode(buf);
            sortedByBytes.Add((buf, entry));
        }
        sortedByBytes.Sort((x, y) => x.encoded.AsSpan().SequenceCompareTo(y.encoded.AsSpan()));

        // Both orderings should produce the same sequence
        for (int i = 0; i < entries.Length; i++)
        {
            Assert.That(sortedByCompare[i].CompareTo(sortedByBytes[i].entry), Is.EqualTo(0));
        }
    }

    [Test]
    public void Entry_type_roundtrip() =>
        Assert.Multiple(() =>
        {
            Assert.That(IndexEntry.CreateBlock(TestItem.KeccakA, 1).Type, Is.EqualTo(IndexEntryType.Block));
            Assert.That(IndexEntry.CreateTransaction(TestItem.KeccakA, 1, 0, 0).Type, Is.EqualTo(IndexEntryType.Transaction));
            Assert.That(IndexEntry.CreateLogAddress(TestItem.AddressA, 1, 0, 0).Type, Is.EqualTo(IndexEntryType.LogAddress));
            Assert.That(IndexEntry.CreateLogTopic(0, TestItem.KeccakA, 1, 0, 0).Type, Is.EqualTo(IndexEntryType.LogTopic0));
            Assert.That(IndexEntry.CreateLogTopic(1, TestItem.KeccakA, 1, 0, 0).Type, Is.EqualTo(IndexEntryType.LogTopic1));
            Assert.That(IndexEntry.CreateLogTopic(2, TestItem.KeccakA, 1, 0, 0).Type, Is.EqualTo(IndexEntryType.LogTopic2));
            Assert.That(IndexEntry.CreateLogTopic(3, TestItem.KeccakA, 1, 0, 0).Type, Is.EqualTo(IndexEntryType.LogTopic3));
        });
}
