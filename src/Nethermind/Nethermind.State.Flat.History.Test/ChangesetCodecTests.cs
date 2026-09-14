// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class ChangesetCodecTests
{
    [Test]
    public void AnAccountEntry_CarriesOnlyTheFieldsTheTransactionChanged()
    {
        byte[] buffer = new byte[ChangesetCodec.MaxAccountEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, TestItem.AddressA, [0x2a], [], [], deleted: false, storageCleared: false);

        ChangesetCodec.Enumerator entries = ChangesetCodec.Read(buffer.AsSpan(0, length));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.MoveNext(), Is.True);
            Assert.That(entries.Kind, Is.EqualTo(ChangesetCodec.AccountKind));
            Assert.That(entries.Address.ToArray(), Is.EqualTo(TestItem.AddressA.Bytes.ToArray()));
            Assert.That(entries.Balance.ToArray(), Is.EqualTo(new byte[] { 0x2a }));
            Assert.That(entries.Nonce.IsEmpty, Is.True, "a field the transaction left alone keeps the value the previous block holds, so the entry must not restate it");
            Assert.That(entries.CodeHash.IsEmpty, Is.True);
            Assert.That(entries.Deleted, Is.False);
            Assert.That(entries.MoveNext(), Is.False);
        }
    }

    [Test]
    public void AnAccountEntry_CarriesEveryFieldAtOnce()
    {
        byte[] codeHash = TestItem.KeccakA.BytesToArray();
        byte[] buffer = new byte[ChangesetCodec.MaxAccountEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, TestItem.AddressB, [0x01, 0x02], [0x07], codeHash, deleted: false, storageCleared: true);

        ChangesetCodec.Enumerator entries = ChangesetCodec.Read(buffer.AsSpan(0, length));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.MoveNext(), Is.True);
            Assert.That(entries.Balance.ToArray(), Is.EqualTo(new byte[] { 0x01, 0x02 }));
            Assert.That(entries.Nonce.ToArray(), Is.EqualTo(new byte[] { 0x07 }));
            Assert.That(entries.CodeHash.ToArray(), Is.EqualTo(codeHash));
            Assert.That(entries.StorageCleared, Is.True);
        }
    }

    [Test]
    public void ADeletedAccount_ReadsAsStorageCleared()
    {
        byte[] buffer = new byte[ChangesetCodec.MaxAccountEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, TestItem.AddressA, [], [], [], deleted: true, storageCleared: false);

        ChangesetCodec.Enumerator entries = ChangesetCodec.Read(buffer.AsSpan(0, length));
        entries.MoveNext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Deleted, Is.True);
            Assert.That(entries.StorageCleared, Is.True, "an account that is gone took its slots with it, whether or not the writer said so twice");
        }
    }

    [TestCase(0, 0, TestName = "ClearedSlotOfAZeroIndex")]
    [TestCase(1, 32, TestName = "ShortIndexFullValue")]
    [TestCase(32, 1, TestName = "FullIndexShortValue")]
    public void AStorageEntry_RoundTrips(int indexLength, int valueLength)
    {
        byte[] index = Filled(indexLength, 0xA0);
        byte[] value = Filled(valueLength, 0xB0);
        byte[] buffer = new byte[ChangesetCodec.MaxStorageEntryLength];
        int length = ChangesetCodec.WriteStorage(buffer, TestItem.AddressB, index, value);

        ChangesetCodec.Enumerator entries = ChangesetCodec.Read(buffer.AsSpan(0, length));
        entries.MoveNext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Kind, Is.EqualTo(ChangesetCodec.StorageKind));
            Assert.That(entries.Address.ToArray(), Is.EqualTo(TestItem.AddressB.Bytes.ToArray()));
            Assert.That(entries.Index.ToArray(), Is.EqualTo(index));
            Assert.That(entries.Value.ToArray(), Is.EqualTo(value));
        }
    }

    [Test]
    public void MixedEntries_KeepTheirOrder()
    {
        byte[] buffer = new byte[3 * ChangesetCodec.MaxStorageEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, TestItem.AddressA, [0x11], [], [], deleted: false, storageCleared: false);
        length += ChangesetCodec.WriteStorage(buffer.AsSpan(length), TestItem.AddressB, [0x01], [0x22]);
        length += ChangesetCodec.WriteAccount(buffer.AsSpan(length), TestItem.AddressC, [], [], [], deleted: true, storageCleared: false);

        ChangesetCodec.Enumerator entries = ChangesetCodec.Read(buffer.AsSpan(0, length));
        entries.MoveNext();
        entries.MoveNext();
        entries.MoveNext();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Address.ToArray(), Is.EqualTo(TestItem.AddressC.Bytes.ToArray()));
            Assert.That(entries.Deleted, Is.True);
            Assert.That(entries.MoveNext(), Is.False);
        }
    }

    [Test]
    public void ATruncatedRow_IsRefused()
    {
        byte[] buffer = new byte[ChangesetCodec.MaxAccountEntryLength];
        int length = ChangesetCodec.WriteAccount(buffer, TestItem.AddressA, [0x11, 0x22], [], [], deleted: false, storageCleared: false);

        Assert.That(() => Read(buffer.AsSpan(0, length - 1)), Throws.InstanceOf<InvalidDataException>(),
            "a row read back short is corruption, not an empty changeset: it must not decode to a shorter but plausible write list");
    }

    private static void Read(ReadOnlySpan<byte> changeset)
    {
        ChangesetCodec.Enumerator entries = ChangesetCodec.Read(changeset);
        while (entries.MoveNext())
        {
        }
    }

    private static byte[] Filled(int length, byte first)
    {
        byte[] bytes = new byte[length];
        if (length > 0) bytes[0] = first;
        return bytes;
    }
}
