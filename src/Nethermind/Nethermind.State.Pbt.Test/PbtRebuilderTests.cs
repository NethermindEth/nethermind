// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <param name="layout">
/// The rebuild is a producer of its own — it folds windows of sorted leaves rather than a block's
/// writes — so it is run under every tiling, against the same reference root.
/// </param>
[TestFixture(PbtTrieLayout.ClusteredFourLevelInterleaved)]
[TestFixture(PbtTrieLayout.SixLevelInterleaved)]
[TestFixture(PbtTrieLayout.EightLevelInterleaved)]
public class PbtRebuilderTests(PbtTrieLayout layout)
{
    private PbtConfig Config => new() { TrieNodeLayout = layout };

    private static List<RebuildEntry> BuildFixture(Dictionary<string, byte[]> model)
    {
        // > 128 chunks (3968 bytes) so overflow chunks land in the content-addressed code zone
        byte[] bigCode = new byte[5000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63; // PUSH4, to exercise the chunk PUSHDATA offsets
        byte[] smallCode = Bytes.FromHexString("0x60016002");

        List<RebuildEntry> leaves = [];

        void AddAccount(Address address, ulong nonce, in UInt256 balance, byte[]? code)
        {
            PbtReferenceModel.SetAccount(model, address, nonce, balance, code);
            Account account = code is { Length: > 0 }
                ? new Account(nonce, balance).WithChangedCodeHash(Keccak.Compute(code))
                : new Account(nonce, balance);
            PbtTestLeaves.AddAccount(leaves, address, account, code is { Length: > 0 } ? code : null);
        }

        void AddSlot(Address address, in UInt256 slot, in UInt256 value)
        {
            PbtReferenceModel.SetSlot(model, address, slot, value);
            PbtTestLeaves.AddSlot(leaves, address, slot, value);
        }

        AddAccount(TestItem.AddressA, 1, 100, null);                  // EOA
        AddAccount(TestItem.AddressB, 0, 42, bigCode);               // overflow-code contract
        AddSlot(TestItem.AddressB, 5, 0xAB);                        // header-region slot (< 64)
        AddSlot(TestItem.AddressB, 70, 0x07);                       // storage-zone slot (>= 64)
        AddSlot(TestItem.AddressB, 1000, 0x1234);
        AddAccount(TestItem.AddressC, 2, 7, smallCode);             // small contract, no overflow
        AddSlot(TestItem.AddressC, 3, 0x99);

        return leaves;
    }

    private async Task<ValueHash256> Fold(List<RebuildEntry> leaves, int flushEntryInterval, int maxWindowStems, StateId targetState, PbtRocksDbPersistence target)
    {
        ArrayPoolList<RebuildEntry> entries = new(leaves.Count); // ownership passes to Rebuild, which disposes it
        foreach (RebuildEntry leaf in leaves) entries.Add(leaf);

        PbtRebuilder rebuilder = new(target, LimboLogs.Instance, Config) { FlushEntryInterval = flushEntryInterval, MaxWindowStems = maxWindowStems };
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateUnbounded<ArrayPoolList<RebuildEntry>>();
        channel.Writer.TryWrite(entries);
        channel.Writer.Complete();

        return await rebuilder.Rebuild(channel.Reader, targetState, CancellationToken.None);
    }

    // A window seals on whichever of its two bounds it reaches first, and seals on a stem boundary
    // either way: a leaf bound of 1 commits a window per stem, a stem bound of 1 the same, and larger
    // values of either fold the state in fewer windows. All must yield the same root — batching
    // invariance.
    [TestCase(1, int.MaxValue)]
    [TestCase(3, int.MaxValue)]
    [TestCase(1_000, int.MaxValue)]
    [TestCase(int.MaxValue, 1)]
    [TestCase(int.MaxValue, 2)]
    public async Task Rebuild_matches_reference_root(int flushEntryInterval, int maxWindowStems)
    {
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model);
        PbtTestLeaves.SortByTreeKey(leaves);

        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);

        // the header root the source claims is unrelated to the tree root the fold produces, so the
        // two must be recorded separately rather than one standing in for the other
        StateId targetState = new(7, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = await Fold(leaves, flushEntryInterval, maxWindowStems, targetState, target);

        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "rebuilt root must match the EIP reference tree");

        using IPbtPersistence.IReader reader = target.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(targetState), "persisted state pointer must advance to the rebuilt state");
        Assert.That(reader.CurrentTreeRoot, Is.EqualTo(root), "and record the tree root beside it");
    }

    /// <summary>
    /// Ordering is what keeps each window a contiguous stem range, but it is not what makes the fold
    /// correct: the same leaves in any order must fold to the same root. Pinning that keeps the
    /// importer free to reorder its passes.
    /// </summary>
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(1_000)]
    public async Task Rebuild_is_independent_of_entry_order(int flushEntryInterval)
    {
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model);

        Random random = new(42);
        for (int i = leaves.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (leaves[i], leaves[j]) = (leaves[j], leaves[i]);
        }

        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);

        ValueHash256 root = await Fold(leaves, flushEntryInterval, int.MaxValue, new StateId(7, TestItem.KeccakA.ValueHash256), target);

        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
    }

    [Test]
    public async Task Rebuild_empty_source_produces_empty_tree()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db, Config);
        PbtRebuilder rebuilder = new(target, LimboLogs.Instance, Config);

        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateUnbounded<ArrayPoolList<RebuildEntry>>();
        channel.Writer.Complete();

        StateId targetState = new(3, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = await rebuilder.Rebuild(channel.Reader, targetState, CancellationToken.None);

        Assert.That(root, Is.EqualTo(default(ValueHash256)), "an empty tree is 32 zero bytes");
        using IPbtPersistence.IReader reader = target.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(targetState));
    }
}
