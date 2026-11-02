// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ChangesetCollectorTests
{
    private static readonly byte[] Code = [0x60, 0x00, 0x55];

    [Test]
    public void ATransaction_IsCollectedAndPackedWithoutAllocating_OnceTheCollectorIsWarm()
    {
        ChangesetCollector collector = new();
        Address[] accounts = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD, TestItem.AddressE, TestItem.AddressF];
        StorageCell slot = new(TestItem.AddressA, 1);
        byte[] value = [0x01];
        int packed = Collect(collector, accounts, slot, value);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) packed += Collect(collector, accounts, slot, value);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        collector.Release();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(packed, Is.Positive, "precondition: every transaction packs its changes");
            Assert.That(allocated, Is.Zero, "the account changes are kept in place and reused, not allocated per account per transaction");
        }
    }

    [Test]
    public void APackedTransaction_HoldsTheLastWriteOfEachField_AndNothingOfTheTransactionBefore()
    {
        ChangesetCollector collector = new();
        collector.Balance(TestItem.AddressA, 1);
        collector.Nonce(TestItem.AddressA, 9);
        collector.Code(TestItem.AddressA, Code);
        collector.StorageCleared(TestItem.AddressA);
        collector.Pack();
        collector.Reset();

        collector.Nonce(TestItem.AddressA, 3);
        collector.Balance(TestItem.AddressB, 5);
        collector.Balance(TestItem.AddressB, 7);
        collector.Deleted(TestItem.AddressC);
        ChangesetCodec.Enumerator entries = ChangesetCodec.Read(collector.Pack());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.MoveNext(), Is.True);
            Assert.That(entries.Address, Is.SequenceEqualTo(TestItem.AddressA.Bytes));
            Assert.That(entries.Nonce, Is.SequenceEqualTo(new byte[] { 3 }));
            Assert.That(entries.Balance.IsEmpty, Is.True, "the balance belonged to the transaction before the reset");
            Assert.That(entries.CodeHash.IsEmpty, Is.True, "so did the code");
            Assert.That(entries.StorageCleared, Is.False, "and the clear");

            Assert.That(entries.MoveNext(), Is.True);
            Assert.That(entries.Address, Is.SequenceEqualTo(TestItem.AddressB.Bytes));
            Assert.That(entries.Balance, Is.SequenceEqualTo(new byte[] { 7 }), "the last write wins");
            Assert.That(entries.Nonce.IsEmpty, Is.True);

            Assert.That(entries.MoveNext(), Is.True);
            Assert.That(entries.Address, Is.SequenceEqualTo(TestItem.AddressC.Bytes));
            Assert.That(entries.Deleted, Is.True);
            Assert.That(entries.MoveNext(), Is.False);
        }

        collector.Reset();
        collector.Code(TestItem.AddressD, Code);
        ChangesetCodec.Enumerator code = ChangesetCodec.Read(collector.Pack());
#pragma warning disable NUnit2045 // The assertions advance or mutate the same state before checking it; preserve fail-fast ordering.
        Assert.That(code.MoveNext(), Is.True, "precondition: the code change is packed");
#pragma warning restore NUnit2045
        Assert.That(code.CodeHash.ToArray(), Is.EqualTo(ValueKeccak.Compute(Code).ToByteArray()), "the code is carried as its hash");
        collector.Release();
    }

    private static int Collect(ChangesetCollector collector, Address[] accounts, in StorageCell slot, byte[] value)
    {
        collector.Reset();
        for (int i = 0; i < accounts.Length; i++)
        {
            collector.Balance(accounts[i], (UInt256)(i + 1));
            if (i == 0) collector.Nonce(accounts[i], 1);
        }

        collector.Storage(slot, value);
        return collector.Pack().Length;
    }
}
