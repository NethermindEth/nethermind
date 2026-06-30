// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
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

        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
        new System.Span<byte>(rlp).CopyTo(owner.Memory.Span);
        return new StateDiffRecord(owner, owner.Memory[..length]);
    }

    [Test]
    public void Round_trips_all_change_kinds_slots_and_code()
    {
        byte[] code = Bytes.FromHexString("0x6001600101");
        Hash256 codeHash = Keccak.Compute(code);
        Account accountA = new(7, 13.Ether, Keccak.EmptyTreeHash, codeHash);
        Account accountB = new(1, 5.Ether);

        StateDiffRecordBuilder builder = new();
        builder.SetAccount(TestItem.AddressA, accountA);                                  // Set, no storage
        builder.SetAccount(TestItem.AddressB, accountB);                                  // Set, with storage + clear
        builder.ClearStorage(TestItem.AddressB);
        builder.SetSlot(TestItem.AddressB, (UInt256)1, Bytes.FromHexString("0x0abc"));
        builder.SetSlot(TestItem.AddressB, (UInt256)2, []);                               // cleared slot
        builder.SetSlot(TestItem.AddressB, UInt256.MaxValue, Bytes.FromHexString("0xff"));
        builder.SetAccount(TestItem.AddressC, null);                                      // Deleted
        builder.ClearStorage(TestItem.AddressC);
        builder.SetSlot(TestItem.AddressD, (UInt256)42, Bytes.FromHexString("0xdeadbeef")); // storage-only (None)
        builder.AddCode(codeHash.ValueHash256, code);

        using StateDiffRecord record = Build(builder, 123_456, TestItem.KeccakA);

        Assert.Multiple(() =>
        {
            Assert.That(record.Version, Is.EqualTo(StateDiffRecord.CurrentVersion));
            Assert.That(record.BlockNumber, Is.EqualTo(123_456UL));
            Assert.That(record.StateRoot, Is.EqualTo(TestItem.KeccakA));
            Assert.That(record.HasCodes, Is.True);
        });

        int accountCount = 0;
        int bSlots = 0;
        foreach (StateDiffRecord.AccountView account in record.Accounts)
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
    public void Concurrent_storage_writes_are_recorded_safely()
    {
        // Mirrors the world state's parallel per-contract storage flush: many addresses written concurrently.
        StateDiffRecordBuilder builder = new();
        const int contracts = 256;

        Parallel.For(0, contracts, i =>
        {
            Address address = AddressFromIndex(i);
            builder.ClearStorage(address);
            builder.SetSlot(address, (UInt256)i, Bytes.FromHexString("0x01"));
        });

        using StateDiffRecord record = Build(builder, 1, TestItem.KeccakA);

        int recorded = 0;
        foreach (StateDiffRecord.AccountView account in record.Accounts)
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

        int accounts = 0;
        foreach (StateDiffRecord.AccountView _ in record.Accounts) accounts++;
        int codes = 0;
        foreach (StateDiffRecord.CodeView _ in record.Codes) codes++;

        Assert.Multiple(() =>
        {
            Assert.That(accounts, Is.Zero);
            Assert.That(codes, Is.Zero);
            Assert.That(record.HasCodes, Is.False);
            Assert.That(record.BlockNumber, Is.Zero);
            Assert.That(record.StateRoot, Is.EqualTo(Keccak.EmptyTreeHash));
        });
    }
}
