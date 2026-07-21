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
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtRebuilderTests
{
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

    private static async Task<ValueHash256> Fold(List<RebuildEntry> leaves, int flushEntryInterval, ulong blockNumber, PbtRocksDbPersistence target)
    {
        ArrayPoolList<RebuildEntry> entries = new(leaves.Count); // ownership passes to Rebuild, which disposes it
        foreach (RebuildEntry leaf in leaves) entries.Add(leaf);

        PbtRebuilder rebuilder = new(target, LimboLogs.Instance, new PbtConfig()) { FlushEntryInterval = flushEntryInterval };
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateUnbounded<ArrayPoolList<RebuildEntry>>();
        channel.Writer.TryWrite(entries);
        channel.Writer.Complete();

        return await rebuilder.Rebuild(channel.Reader, blockNumber, CancellationToken.None);
    }

    // A flush interval of 1 forces one committed window per leaf, exercising the cross-window merge
    // (e.g. a header stem folded by a BASIC_DATA leaf, then again by a later header-slot leaf); larger
    // values fold the whole state in fewer windows. All must yield the same root — batching invariance.
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(1_000)]
    public async Task Rebuild_matches_reference_root(int flushEntryInterval)
    {
        Dictionary<string, byte[]> model = [];
        List<RebuildEntry> leaves = BuildFixture(model);
        PbtTestLeaves.SortByTreeKey(leaves);

        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db);

        const ulong blockNumber = 7;
        ValueHash256 root = await Fold(leaves, flushEntryInterval, blockNumber, target);

        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "rebuilt root must match the EIP reference tree");

        using IPbtPersistence.IReader reader = target.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(blockNumber, root)), "persisted state pointer must advance to the rebuilt state");
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
        PbtRocksDbPersistence target = new(db);

        ValueHash256 root = await Fold(leaves, flushEntryInterval, blockNumber: 7, target);

        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)));
    }

    [Test]
    public async Task Rebuild_empty_source_produces_empty_tree()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db);
        PbtRebuilder rebuilder = new(target, LimboLogs.Instance, new PbtConfig());

        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateUnbounded<ArrayPoolList<RebuildEntry>>();
        channel.Writer.Complete();

        ValueHash256 root = await rebuilder.Rebuild(channel.Reader, blockNumber: 3, CancellationToken.None);

        Assert.That(root, Is.EqualTo(default(ValueHash256)), "an empty tree is 32 zero bytes");
        using IPbtPersistence.IReader reader = target.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(3, default)));
    }
}
