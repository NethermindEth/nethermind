// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.StateDiffArchive.Data;
using NUnit.Framework;

namespace Nethermind.StateDiffArchive.Test;

[Parallelizable(ParallelScope.All)]
public class StateDiffRecordTests
{
    private static StateDiffRecord Build(StateDiffRecordBuilder builder, ulong blockNumber, Hash256 stateRoot)
    {
        int length = builder.GetLength(blockNumber, stateRoot);
        byte[] rlp = new byte[length];
        RlpWriter writer = new(rlp);
        builder.WriteTo(ref writer, blockNumber, stateRoot);

        return Wrap(rlp);
    }

    private static StateDiffRecord Wrap(byte[] rlp)
    {
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(rlp.Length);
        new System.Span<byte>(rlp).CopyTo(owner.Memory.Span);
        return new StateDiffRecord(owner, owner.Memory[..rlp.Length]);
    }

    // Collects the record's batches as (accountCount, per-account slot map) so ref-struct views don't escape.
    private static List<(int Accounts, Dictionary<UInt256, byte[]> Slots)> ReadBatches(StateDiffRecord record)
    {
        List<(int, Dictionary<UInt256, byte[]>)> batches = [];
        foreach (StateDiffRecord.WriteBatchView batch in record.Batches)
        {
            int accounts = 0;
            Dictionary<UInt256, byte[]> slots = [];
            foreach (StateDiffRecord.AccountView account in batch.Accounts)
            {
                accounts++;
                foreach (StateDiffRecord.SlotView slot in account.Slots) slots[slot.Index] = slot.Value.ToArray();
            }
            batches.Add((accounts, slots));
        }
        return batches;
    }

    private static StateDiffRecord.WriteBatchView SingleBatch(StateDiffRecord record)
    {
        StateDiffRecord.WriteBatchView single = default;
        int count = 0;
        foreach (StateDiffRecord.WriteBatchView batch in record.Batches) { single = batch; count++; }
        Assert.That(count, Is.EqualTo(1), "expected exactly one write batch");
        return single;
    }

    [Test]
    public void Round_trips_all_change_kinds_slots_and_code()
    {
        byte[] code = Bytes.FromHexString("0x6001600101");
        Hash256 codeHash = Keccak.Compute(code);
        Account accountA = new(7, 13.Ether, Keccak.EmptyTreeHash, codeHash);
        Account accountB = new(1, 5.Ether);

        StateDiffRecordBuilder builder = new();
        StateDiffRecordBuilder.BatchBuilder batch = builder.StartBatch();
        batch.SetAccount(TestItem.AddressA, accountA);                                  // Set, no storage
        batch.SetAccount(TestItem.AddressB, accountB);                                  // Set, with storage + clear
        batch.ClearStorage(TestItem.AddressB);
        batch.SetSlot(TestItem.AddressB, (UInt256)1, Bytes.FromHexString("0x0abc"));
        batch.SetSlot(TestItem.AddressB, (UInt256)2, []);                               // cleared slot
        batch.SetSlot(TestItem.AddressB, UInt256.MaxValue, Bytes.FromHexString("0xff"));
        batch.SetAccount(TestItem.AddressC, null);                                      // Deleted
        batch.ClearStorage(TestItem.AddressC);
        batch.SetSlot(TestItem.AddressD, (UInt256)42, Bytes.FromHexString("0xdeadbeef")); // storage-only (None)
        builder.AddCode(codeHash.ValueHash256, code);

        using StateDiffRecord record = Build(builder, 123_456, TestItem.KeccakA);
        StateDiffRecord.WriteBatchView single = SingleBatch(record);

        Assert.Multiple(() =>
        {
            Assert.That(record.Version, Is.EqualTo(StateDiffRecord.CurrentVersion));
            Assert.That(record.BlockNumber, Is.EqualTo(123_456UL));
            Assert.That(record.StateRoot, Is.EqualTo(TestItem.KeccakA));
            Assert.That(record.HasCodes, Is.True);
            Assert.That(single.CountAccounts(), Is.EqualTo(4));
        });

        int accountCount = 0;
        int bSlots = 0;
        foreach (StateDiffRecord.AccountView account in single.Accounts)
        {
            accountCount++;
            if (account.Address.Equals(TestItem.AddressA))
            {
                Assert.That(account.Change, Is.EqualTo(AccountChangeKind.Set));
                Assert.That(account.Account, Is.EqualTo(accountA));
                Assert.That(account.StorageCleared, Is.False);
                Assert.That(account.HasSlots, Is.False);
            }
            else if (account.Address.Equals(TestItem.AddressB))
            {
                Assert.That(account.Change, Is.EqualTo(AccountChangeKind.Set));
                Assert.That(account.Account, Is.EqualTo(accountB));
                Assert.That(account.StorageCleared, Is.True);
                Assert.That(account.Slots.Count(), Is.EqualTo(3));
                foreach (StateDiffRecord.SlotView slot in account.Slots)
                {
                    bSlots++;
                    byte[] expected = slot.Index == (UInt256)1 ? Bytes.FromHexString("0x0abc")
                        : slot.Index == (UInt256)2 ? []
                        : Bytes.FromHexString("0xff");
                    Assert.That(slot.Value.ToArray(), Is.EqualTo(expected), $"slot {slot.Index}");
                }
            }
            else if (account.Address.Equals(TestItem.AddressC))
            {
                Assert.That(account.Change, Is.EqualTo(AccountChangeKind.Deleted));
                Assert.That(account.Account, Is.Null);
                Assert.That(account.StorageCleared, Is.True);
            }
            else if (account.Address.Equals(TestItem.AddressD))
            {
                Assert.That(account.Change, Is.EqualTo(AccountChangeKind.None));
                Assert.That(account.Account, Is.Null);
                Assert.That(account.HasSlots, Is.True);
                Assert.That(account.Slots.Count(), Is.EqualTo(1));
            }
        }

        int codeCount = 0;
        foreach (StateDiffRecord.CodeView entry in record.Codes)
        {
            codeCount++;
            Assert.That(entry.CodeHash, Is.EqualTo(codeHash.ValueHash256));
            Assert.That(entry.Code.ToArray(), Is.EqualTo(code));
        }

        Assert.Multiple(() =>
        {
            Assert.That(accountCount, Is.EqualTo(4));
            Assert.That(bSlots, Is.EqualTo(3));
            Assert.That(codeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Dedupes_repeated_slot_writes_and_clear_drops_earlier_slots_within_a_batch()
    {
        StateDiffRecordBuilder builder = new();
        StateDiffRecordBuilder.BatchBuilder batch = builder.StartBatch();

        // A: same slot written twice within the flush -> last value wins, single entry.
        batch.SetSlot(TestItem.AddressA, (UInt256)1, Bytes.FromHexString("0x01"));
        batch.SetSlot(TestItem.AddressA, (UInt256)1, Bytes.FromHexString("0x02"));

        // B: slot set, then storage cleared (self-destruct), then a new slot set -> only the post-clear slot.
        batch.SetSlot(TestItem.AddressB, (UInt256)7, Bytes.FromHexString("0xaa"));
        batch.ClearStorage(TestItem.AddressB);
        batch.SetSlot(TestItem.AddressB, (UInt256)8, Bytes.FromHexString("0xbb"));

        // Same code recorded twice -> single entry.
        byte[] code = Bytes.FromHexString("0x60016002");
        builder.AddCode(Keccak.Compute(code).ValueHash256, code);
        builder.AddCode(Keccak.Compute(code).ValueHash256, code);

        using StateDiffRecord record = Build(builder, 1, TestItem.KeccakA);

        foreach (StateDiffRecord.AccountView account in SingleBatch(record).Accounts)
        {
            if (account.Address.Equals(TestItem.AddressA))
            {
                Assert.That(account.StorageCleared, Is.False);
                Assert.That(account.Slots.Count(), Is.EqualTo(1));
                foreach (StateDiffRecord.SlotView slot in account.Slots)
                    Assert.That(slot.Value.ToArray(), Is.EqualTo(Bytes.FromHexString("0x02")));
            }
            else if (account.Address.Equals(TestItem.AddressB))
            {
                Assert.That(account.StorageCleared, Is.True);
                Assert.That(account.Slots.Count(), Is.EqualTo(1));
                foreach (StateDiffRecord.SlotView slot in account.Slots)
                {
                    Assert.That(slot.Index, Is.EqualTo((UInt256)8)); // slot 7 dropped by the clear
                    Assert.That(slot.Value.ToArray(), Is.EqualTo(Bytes.FromHexString("0xbb")));
                }
            }
        }

        int codes = 0;
        foreach (StateDiffRecord.CodeView _ in record.Codes) codes++;
        Assert.That(codes, Is.EqualTo(1));
    }

    [Test]
    public void Records_write_batches_in_order_without_merging()
    {
        StateDiffRecordBuilder builder = new();

        // Batch 1 (as a first transaction's flush would): set A, write slot 1 = 0x01.
        StateDiffRecordBuilder.BatchBuilder b1 = builder.StartBatch();
        b1.SetAccount(TestItem.AddressA, new Account(1, 1.Ether));
        b1.SetSlot(TestItem.AddressA, (UInt256)1, Bytes.FromHexString("0x01"));

        // Batch 2 (a second transaction's flush): the SAME slot rewritten -> preserved as a distinct batch,
        // not merged away, which is the whole point of the per-batch format for pre-Byzantium blocks.
        StateDiffRecordBuilder.BatchBuilder b2 = builder.StartBatch();
        b2.SetSlot(TestItem.AddressA, (UInt256)1, Bytes.FromHexString("0x02"));

        using StateDiffRecord record = Build(builder, 5, TestItem.KeccakA);
        List<(int Accounts, Dictionary<UInt256, byte[]> Slots)> batches = ReadBatches(record);

        Assert.Multiple(() =>
        {
            Assert.That(batches, Has.Count.EqualTo(2));
            Assert.That(batches[0].Accounts, Is.EqualTo(1));
            Assert.That(batches[0].Slots[(UInt256)1], Is.EqualTo(Bytes.FromHexString("0x01")));
            Assert.That(batches[1].Slots[(UInt256)1], Is.EqualTo(Bytes.FromHexString("0x02")));
        });
    }

    [Test]
    public void Reads_v1_record_as_a_single_batch()
    {
        using StateDiffRecord record = Wrap(EncodeV1Deleted(7, TestItem.KeccakB, TestItem.AddressC));

        Assert.That(record.Version, Is.EqualTo((byte)1));

        int batches = 0, accounts = 0;
        foreach (StateDiffRecord.WriteBatchView batch in record.Batches)
        {
            batches++;
            foreach (StateDiffRecord.AccountView account in batch.Accounts)
            {
                accounts++;
                Assert.That(account.Address, Is.EqualTo(TestItem.AddressC));
                Assert.That(account.Change, Is.EqualTo(AccountChangeKind.Deleted));
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.EqualTo(1)); // a v1 record surfaces as exactly one batch
            Assert.That(accounts, Is.EqualTo(1));
            Assert.That(record.BlockNumber, Is.EqualTo(7UL));
            Assert.That(record.StateRoot, Is.EqualTo(TestItem.KeccakB));
        });
    }

    [Test]
    public void Rejects_unknown_version()
    {
        byte[] rlp = EncodeHeaderOnly(version: 3, blockNumber: 0, stateRoot: TestItem.KeccakA);
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(rlp.Length);
        new System.Span<byte>(rlp).CopyTo(owner.Memory.Span);
        try
        {
            Assert.That(() => new StateDiffRecord(owner, owner.Memory[..rlp.Length]), Throws.InstanceOf<RlpException>());
        }
        finally { owner.Dispose(); }
    }

    [Test]
    public void Concurrent_storage_writes_are_recorded_safely()
    {
        // Mirrors the world state's parallel per-contract storage flush within one batch: many addresses written concurrently.
        StateDiffRecordBuilder builder = new();
        StateDiffRecordBuilder.BatchBuilder batch = builder.StartBatch();
        const int contracts = 256;

        Parallel.For(0, contracts, i =>
        {
            Address address = AddressFromIndex(i);
            batch.ClearStorage(address);
            batch.SetSlot(address, (UInt256)i, Bytes.FromHexString("0x01"));
        });

        using StateDiffRecord record = Build(builder, 1, TestItem.KeccakA);

        int recorded = 0;
        foreach (StateDiffRecord.AccountView account in SingleBatch(record).Accounts)
        {
            recorded++;
            Assert.That(account.StorageCleared, Is.True);
            Assert.That(account.HasSlots, Is.True);
        }

        Assert.That(recorded, Is.EqualTo(contracts));
    }

    private static Address AddressFromIndex(int i)
    {
        byte[] bytes = new byte[Address.Size];
        BinaryPrimitives.WriteInt32BigEndian(new Span<byte>(bytes, Address.Size - 4, 4), i);
        return new Address(bytes);
    }

    [Test]
    public void Round_trips_empty_record()
    {
        using StateDiffRecord record = Build(new StateDiffRecordBuilder(), 0, Keccak.EmptyTreeHash);

        int batches = 0;
        foreach (StateDiffRecord.WriteBatchView _ in record.Batches) batches++;
        int codes = 0;
        foreach (StateDiffRecord.CodeView _ in record.Codes) codes++;

        Assert.Multiple(() =>
        {
            Assert.That(batches, Is.Zero);
            Assert.That(codes, Is.Zero);
            Assert.That(record.HasCodes, Is.False);
            Assert.That(record.BlockNumber, Is.Zero);
            Assert.That(record.StateRoot, Is.EqualTo(Keccak.EmptyTreeHash));
        });
    }

    // A minimal legacy (v1) record: [1, blockNumber, stateRoot, [ [addr, Deleted, false, []] ], []].
    // Uses the same RlpWriter conventions as the builder so encode/decode agree; the v1 accounts list has no
    // per-batch wrapper, so reading it as a single batch exercises the back-compat path.
    private static byte[] EncodeV1Deleted(ulong blockNumber, Hash256 stateRoot, Address address)
    {
        int diffContent = Rlp.LengthOf(address) + Rlp.LengthOf((byte)AccountChangeKind.Deleted)
                          + Rlp.LengthOf((byte)0) + Rlp.LengthOfSequence(0);
        int accountsContent = Rlp.LengthOfSequence(diffContent);
        int content = Rlp.LengthOf((byte)1) + Rlp.LengthOf(blockNumber) + Rlp.LengthOf(stateRoot)
                      + Rlp.LengthOfSequence(accountsContent) + Rlp.LengthOfSequence(0);

        byte[] rlp = new byte[Rlp.LengthOfSequence(content)];
        RlpWriter w = new(rlp);
        w.StartSequence(content);
        w.Encode((byte)1);
        w.Encode(blockNumber);
        w.Encode(stateRoot);
        w.StartSequence(accountsContent);
        w.StartSequence(diffContent);
        w.Encode(address);
        w.Encode((byte)AccountChangeKind.Deleted);
        w.Encode(false);
        w.StartSequence(0); // empty slots
        w.StartSequence(0); // empty codes
        return rlp;
    }

    private static byte[] EncodeHeaderOnly(byte version, ulong blockNumber, Hash256 stateRoot)
    {
        int content = Rlp.LengthOf(version) + Rlp.LengthOf(blockNumber) + Rlp.LengthOf(stateRoot)
                      + Rlp.LengthOfSequence(0) + Rlp.LengthOfSequence(0);
        byte[] rlp = new byte[Rlp.LengthOfSequence(content)];
        RlpWriter w = new(rlp);
        w.StartSequence(content);
        w.Encode(version);
        w.Encode(blockNumber);
        w.Encode(stateRoot);
        w.StartSequence(0); // empty batches
        w.StartSequence(0); // empty codes
        return rlp;
    }
}
