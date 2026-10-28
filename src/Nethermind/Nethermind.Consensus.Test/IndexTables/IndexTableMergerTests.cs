// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexTableMergerTests
{
    [Test]
    public void Merge_empty_sources_returns_empty()
    {
        List<IndexEntry> result = IndexTableMerger.Merge([]);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Merge_single_source_returns_same_entries()
    {
        List<IndexEntry> source =
        [
            IndexEntry.CreateBlock(TestItem.KeccakA, 1),
            IndexEntry.CreateBlock(TestItem.KeccakB, 2),
        ];

        List<IndexEntry> result = IndexTableMerger.Merge([source]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Count, Is.EqualTo(2));
            Assert.That(result[0].CompareTo(source[0]), Is.EqualTo(0));
            Assert.That(result[1].CompareTo(source[1]), Is.EqualTo(0));
        }
    }

    [Test]
    public void Merge_four_sources_produces_sorted_output()
    {
        List<IndexEntry> s0 = [IndexEntry.CreateBlock(TestItem.KeccakA, 0)];
        List<IndexEntry> s1 = [IndexEntry.CreateBlock(TestItem.KeccakB, 1)];
        List<IndexEntry> s2 = [IndexEntry.CreateBlock(TestItem.KeccakC, 2)];
        List<IndexEntry> s3 = [IndexEntry.CreateBlock(TestItem.KeccakD, 3)];

        List<IndexEntry> result = IndexTableMerger.Merge([s0, s1, s2, s3]);

        Assert.That(result.Count, Is.EqualTo(4));

        for (int i = 1; i < result.Count; i++)
        {
            Assert.That(result[i - 1].CompareTo(result[i]), Is.LessThanOrEqualTo(0),
                $"Entry at index {i - 1} should be <= entry at index {i}");
        }
    }

    [Test]
    public void Merge_interleaved_entries_maintains_sort()
    {
        // Sources with different entry types that interleave when merged
        List<IndexEntry> s0 =
        [
            IndexEntry.CreateBlock(TestItem.KeccakA, 10),
            IndexEntry.CreateTransaction(TestItem.KeccakB, 10, 0, 0),
        ];
        List<IndexEntry> s1 =
        [
            IndexEntry.CreateBlock(TestItem.KeccakC, 11),
            IndexEntry.CreateLogAddress(TestItem.AddressA, 11, 0, 0),
        ];

        List<IndexEntry> result = IndexTableMerger.Merge([s0, s1]);

        Assert.That(result.Count, Is.EqualTo(4));

        // All block entries should come before tx/log entries (type 0 < type 1 < type 2)
        for (int i = 1; i < result.Count; i++)
        {
            Assert.That(result[i - 1].CompareTo(result[i]), Is.LessThanOrEqualTo(0));
        }
    }

    [Test]
    public void Merge_with_empty_source_skips_it()
    {
        List<IndexEntry> s0 = [IndexEntry.CreateBlock(TestItem.KeccakA, 0)];
        List<IndexEntry> empty = [];
        List<IndexEntry> s2 = [IndexEntry.CreateBlock(TestItem.KeccakB, 2)];

        List<IndexEntry> result = IndexTableMerger.Merge([s0, empty, s2]);

        Assert.That(result.Count, Is.EqualTo(2));
    }

    // eip-8304.md "Index table example", the sorted binary encodings.
    private static readonly string[] SpecExampleSortedHex =
    [
        "000042f66a2e9f9c68e223e8d826145d7cfacb00520dba6a9555803121de29790b650000000000000028",
        "000066f42ef12b140e8004ad39a760191457f485204ee6c9f990c33f14014e521f20000000000000002a",
        "0000978ce0036b6d1c62d716045505587d15cc85a1def92f9f450937b6467295e5170000000000000029",
        "0000bf98e6cb26f6ff312586968d1f343a3d3c439a8c5c86233aff2a82f1a68263df0000000000000027",
        "00017046035b326ab22a3142e4416ed4300a28a483a31a1e04cf62bec575b2e7cf09000000000000002b0000000000000000",
        "000175590c9ced72898d6207c917e11b452949043404a1802b893f7717a9c1c8f45d000000000000002a0000000100000002",
        "0001ca2d12d1b8132de09d0d668cc87349dc70134bee3010e03ddb2d83f7160bd6e3000000000000002a0000000000000000",
        "0002c02aaa39b223fe8d0a0e5c4f27ead9083c756cc2000000000000002a0000000000000000",
        "0002c02aaa39b223fe8d0a0e5c4f27ead9083c756cc2000000000000002a0000000100000000",
        "0002dac17f958d2ee523a2206206994597c13d831ec7000000000000002a0000000000000001",
        "0002dac17f958d2ee523a2206206994597c13d831ec7000000000000002b0000000000000000",
        "00037fcf532c15f0a6db0bd6d0e038bea71d30d808c7d98cb3bf7268a95bf5081b65000000000000002a0000000100000000",
        "0003ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef000000000000002a0000000000000000",
        "0003ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef000000000000002a0000000000000001",
        "0003ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef000000000000002b0000000000000000",
        "00040000000000000000000000004a5e9a3bf35df5e0ea4b4cd3289e0111f1deadf7000000000000002a0000000000000000",
        "00040000000000000000000000004a5e9a3bf35df5e0ea4b4cd3289e0111f1deadf7000000000000002b0000000000000000",
        "0004000000000000000000000000f2a5fd4d5bb24b651d1198bc014941a16f6edde5000000000000002a0000000000000001",
        "0004000000000000000000000000f2a5fd4d5bb24b651d1198bc014941a16f6edde5000000000000002a0000000100000000",
        "00050000000000000000000000004a5e9a3bf35df5e0ea4b4cd3289e0111f1deadf7000000000000002a0000000000000001",
        "0005000000000000000000000000ac8cbe20039c2e9547da9107456179bd4f39a734000000000000002b0000000000000000",
        "0005000000000000000000000000f2a5fd4d5bb24b651d1198bc014941a16f6edde5000000000000002a0000000000000000",
    ];

    [Test]
    public void Eip_example_blocks_40_to_43_match_the_eip_encoding_and_root()
    {
        Hash256 h39 = new("0xbf98e6cb26f6ff312586968d1f343a3d3c439a8c5c86233aff2a82f1a68263df");
        Hash256 h40 = new("0x42f66a2e9f9c68e223e8d826145d7cfacb00520dba6a9555803121de29790b65");
        Hash256 h41 = new("0x978ce0036b6d1c62d716045505587d15cc85a1def92f9f450937b6467295e517");
        Hash256 h42 = new("0x66f42ef12b140e8004ad39a760191457f485204ee6c9f990c33f14014e521f20");
        Address weth = new("0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2");
        Address usdt = new("0xdac17f958d2ee523a2206206994597c13d831ec7");
        Hash256 transfer = new("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef");
        Hash256 withdrawal = new("0x7fcf532c15f0a6db0bd6d0e038bea71d30d808c7d98cb3bf7268a95bf5081b65");
        Hash256 alice = PaddedAddress("4a5e9a3bf35df5e0ea4b4cd3289e0111f1deadf7");
        Hash256 bob = PaddedAddress("f2a5fd4d5bb24b651d1198bc014941a16f6edde5");
        Hash256 carol = PaddedAddress("ac8cbe20039c2e9547da9107456179bd4f39a734");
        Hash256 tx42s0 = new("0xca2d12d1b8132de09d0d668cc87349dc70134bee3010e03ddb2d83f7160bd6e3");
        // The EIP's tables print this hash as 0xa75590c9ced728..., its binary listing as 0x75590c9ced72898d...; the binary is hashed.
        Hash256 tx42s1 = new("0x75590c9ced72898d6207c917e11b452949043404a1802b893f7717a9c1c8f45d");
        Hash256 tx43s0 = new("0x7046035b326ab22a3142e4416ed4300a28a483a31a1e04cf62bec575b2e7cf09");

        List<IndexEntry>[] level0 =
        [
            Level0(40, h39, [], []),
            Level0(41, h40, [], []),
            Level0(42, h41, [tx42s0, tx42s1],
            [
                [new LogEntry(weth, [], [transfer, alice, bob]), new LogEntry(usdt, [], [transfer, bob, alice])],
                [new LogEntry(weth, [], [withdrawal, bob])],
            ]),
            Level0(43, h42, [tx43s0], [[new LogEntry(usdt, [], [transfer, alice, carol])]]),
        ];

        List<IndexEntry> merged = IndexTableMerger.Merge(level0);
        string[] actualHex = merged.Select(static e => Hex(Encode(e))).ToArray();
        byte[] root = new byte[32];
        IndexTableRootCalculator.ComputeRoot(merged).ToLittleEndian(root);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualHex, Is.EqualTo(SpecExampleSortedHex));
            Assert.That(Hex(root), Is.EqualTo("806fe74b292d817750501d9ba03377b6acdc2af08c594611e16cfabe4b4e6752"));
        }
    }

    private static List<IndexEntry> Level0(ulong number, Hash256 parentHash, Hash256[] txHashes, LogEntry[][] logsPerTx)
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithParentHash(parentHash).TestObject;
        Transaction[] txs = txHashes.Select(static h => Build.A.Transaction.WithHash(h).TestObject).ToArray();
        TxReceipt[] receipts = logsPerTx.Select(static logs => Build.A.Receipt.WithLogs(logs).TestObject).ToArray();
        List<IndexEntry> entries = [];
        IndexEntryGenerator.GenerateEntries(header, txs, receipts, header.ParentHash, entries);
        entries.Sort();
        return entries;
    }

    private static Hash256 PaddedAddress(string hex20) => new("0x000000000000000000000000" + hex20);

    private static byte[] Encode(IndexEntry entry)
    {
        byte[] buffer = new byte[entry.EncodedLength];
        entry.Encode(buffer);
        return buffer;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
