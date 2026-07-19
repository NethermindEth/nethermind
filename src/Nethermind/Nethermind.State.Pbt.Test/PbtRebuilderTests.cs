// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtRebuilderTests
{
    // A flush interval of 1 forces one committed window per entry, exercising the cross-window merge
    // (e.g. a header stem folded by an account entry, then again by a later header-slot entry); larger
    // values fold the whole state in fewer windows. All must yield the same root — batching invariance.
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(1_000)]
    public async Task Rebuild_matches_reference_root_and_reads_back(int flushEntryInterval)
    {
        // > 128 chunks (3968 bytes) so overflow chunks land in the content-addressed code zone
        byte[] bigCode = new byte[5000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63; // PUSH4, to exercise the chunk PUSHDATA offsets
        byte[] smallCode = Bytes.FromHexString("0x60016002");

        Dictionary<string, byte[]> model = [];
        Dictionary<Address, Account> expectedAccounts = [];
        Dictionary<(Address, UInt256), UInt256> expectedSlots = [];

        // entries are staged through the import's scratch format and read back in tree-key order, which
        // is how the rebuilder is fed in production
        SnapshotableMemDb scratch = new();
        IWriteBatch scratchBatch = scratch.StartWriteBatch();
        PbtImportScratchWriter writer = new(scratchBatch);

        void AddAccount(Address address, ulong nonce, in UInt256 balance, byte[]? code)
        {
            PbtReferenceModel.SetAccount(model, address, nonce, balance, code);
            Account account = code is { Length: > 0 }
                ? new Account(nonce, balance).WithChangedCodeHash(Keccak.Compute(code))
                : new Account(nonce, balance);
            expectedAccounts[address] = account;
            writer.WriteAccount(address, account, AccountDecoder.Slim.Encode(account).Bytes, code is { Length: > 0 } ? code : null, PbtKeyDerivation.AddressKeyHash(address), emitOverflowChunks: true);
        }

        void AddSlot(Address address, in UInt256 slot, in UInt256 value)
        {
            PbtReferenceModel.SetSlot(model, address, slot, value);
            expectedSlots[(address, slot)] = value;
            PbtSlotKeyDeriver deriver = new(address);
            writer.WriteSlot(address, slot, EvmWordSlot.FromStripped(value.ToBigEndian().WithoutLeadingZeros()), ref deriver);
        }

        AddAccount(TestItem.AddressA, 1, 100, null);                  // EOA
        AddAccount(TestItem.AddressB, 0, 42, bigCode);               // overflow-code contract
        AddSlot(TestItem.AddressB, 5, 0xAB);                        // header-region slot (< 64)
        AddSlot(TestItem.AddressB, 70, 0x07);                       // storage-zone slot (>= 64)
        AddSlot(TestItem.AddressB, 1000, 0x1234);
        AddAccount(TestItem.AddressC, 2, 7, smallCode);             // small contract, no overflow
        AddSlot(TestItem.AddressC, 3, 0x99);

        scratchBatch.Dispose();

        ArrayPoolList<RebuildEntry> entries = new(64); // ownership passes to Rebuild, which disposes it
        using (ISortedView view = scratch.GetViewBetween(new byte[PbtImportScratch.KeyLength], Enumerable.Repeat((byte)0xFF, PbtImportScratch.KeyLength + 1).ToArray()))
        {
            while (view.MoveNext()) entries.Add(PbtImportScratch.Decode(view.CurrentKey, view.CurrentValue));
        }

        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence target = new(db);
        PbtRebuilder rebuilder = new(target, LimboLogs.Instance, new PbtConfig()) { FlushEntryInterval = flushEntryInterval };

        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateUnbounded<ArrayPoolList<RebuildEntry>>();
        channel.Writer.TryWrite(entries);
        channel.Writer.Complete();

        const ulong blockNumber = 7;
        ValueHash256 root = await rebuilder.Rebuild(channel.Reader, blockNumber, CancellationToken.None);

        Assert.That(root, Is.EqualTo(PbtReferenceModel.Root(model)), "rebuilt root must match the EIP reference tree");

        using IPbtPersistence.IReader reader = target.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(blockNumber, root)), "persisted state pointer must advance to the rebuilt state");

        foreach ((Address address, Account expected) in expectedAccounts)
        {
            Account? actual = reader.GetAccount(address);
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual!.Nonce, Is.EqualTo(expected.Nonce));
            Assert.That(actual.Balance, Is.EqualTo(expected.Balance));
            Assert.That(actual.CodeHash, Is.EqualTo(expected.CodeHash));
        }

        foreach (((Address address, UInt256 slot), UInt256 value) in expectedSlots)
        {
            EvmWord actual = reader.GetSlot(address, slot);
            Assert.That(EvmWordSlot.AsReadOnlySpan(actual).ToArray(), Is.EqualTo(value.ToBigEndian()));
        }
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
