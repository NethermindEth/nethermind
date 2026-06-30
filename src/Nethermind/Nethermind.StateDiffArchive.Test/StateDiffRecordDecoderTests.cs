// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
public class StateDiffRecordDecoderTests
{
    private static byte[] EncodeDecodeEncode(StateDiffRecord record)
    {
        int length = StateDiffRecordDecoder.Instance.GetLength(record);
        byte[] rlp = new byte[length];
        RlpWriter writer = new(rlp);
        StateDiffRecordDecoder.Instance.Encode(ref writer, record);

        RlpReader reader = new(rlp);
        StateDiffRecord decoded = StateDiffRecordDecoder.Instance.Decode(ref reader);

        int reLength = StateDiffRecordDecoder.Instance.GetLength(decoded);
        byte[] reRlp = new byte[reLength];
        RlpWriter reWriter = new(reRlp);
        StateDiffRecordDecoder.Instance.Encode(ref reWriter, decoded);
        return reRlp;
    }

    [Test]
    public void Round_trips_all_change_kinds_slots_and_code()
    {
        byte[] code = Bytes.FromHexString("0x6001600101");
        Hash256 codeHash = Keccak.Compute(code);

        AccountDiff setWithoutStorage = new(
            TestItem.AddressA, AccountChangeKind.Set,
            new Account(7, 13.Ether, Keccak.EmptyTreeHash, codeHash),
            storageCleared: false, slots: []);

        AccountDiff setWithStorage = new(
            TestItem.AddressB, AccountChangeKind.Set,
            new Account(1, 5.Ether),
            storageCleared: true,
            slots:
            [
                new SlotDiff((UInt256)1, Bytes.FromHexString("0x0abc")),
                new SlotDiff((UInt256)2, []),                                  // cleared slot
                new SlotDiff(UInt256.MaxValue, Bytes.FromHexString("0xff")),
            ]);

        AccountDiff deleted = new(TestItem.AddressC, AccountChangeKind.Deleted, null, storageCleared: true, slots: []);

        AccountDiff storageOnly = new(
            TestItem.AddressD, AccountChangeKind.None, null, storageCleared: false,
            slots: [new SlotDiff((UInt256)42, Bytes.FromHexString("0xdeadbeef"))]);

        StateDiffRecord record = new(
            StateDiffRecord.CurrentVersion,
            blockNumber: 123_456,
            stateRoot: TestItem.KeccakA,
            accounts: [setWithoutStorage, setWithStorage, deleted, storageOnly],
            codes: [new CodeDiff(codeHash, code)]);

        // Encode -> Decode -> Encode must be byte-identical, and the decoded fields must match.
        byte[] first = EncodeDecodeEncode(record);
        byte[] second = EncodeDecodeEncode(record);
        Assert.That(second, Is.EqualTo(first));

        RlpReader reader = new(first);
        StateDiffRecord decoded = StateDiffRecordDecoder.Instance.Decode(ref reader);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Version, Is.EqualTo(record.Version));
            Assert.That(decoded.BlockNumber, Is.EqualTo(record.BlockNumber));
            Assert.That(decoded.StateRoot, Is.EqualTo(record.StateRoot));
            Assert.That(decoded.Accounts, Has.Count.EqualTo(4));
            Assert.That(decoded.Codes, Has.Count.EqualTo(1));
            Assert.That(decoded.Codes[0].CodeHash, Is.EqualTo(codeHash));
            Assert.That(decoded.Codes[0].Code, Is.EqualTo(code));
        });

        AssertAccount(decoded.Accounts[0], setWithoutStorage);
        AssertAccount(decoded.Accounts[1], setWithStorage);
        AssertAccount(decoded.Accounts[2], deleted);
        AssertAccount(decoded.Accounts[3], storageOnly);
    }

    [Test]
    public void Round_trips_empty_record()
    {
        StateDiffRecord record = new(StateDiffRecord.CurrentVersion, 0, Keccak.EmptyTreeHash, [], []);
        RlpReader reader = new(EncodeDecodeEncode(record));
        StateDiffRecord decoded = StateDiffRecordDecoder.Instance.Decode(ref reader);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.Accounts, Is.Empty);
            Assert.That(decoded.Codes, Is.Empty);
            Assert.That(decoded.StateRoot, Is.EqualTo(Keccak.EmptyTreeHash));
        });
    }

    private static void AssertAccount(AccountDiff actual, AccountDiff expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.Address, Is.EqualTo(expected.Address));
            Assert.That(actual.Change, Is.EqualTo(expected.Change));
            Assert.That(actual.Account, Is.EqualTo(expected.Account));
            Assert.That(actual.StorageCleared, Is.EqualTo(expected.StorageCleared));
            Assert.That(actual.Slots, Has.Count.EqualTo(expected.Slots.Count));
        });

        for (int i = 0; i < expected.Slots.Count; i++)
        {
            Assert.That(actual.Slots[i].Index, Is.EqualTo(expected.Slots[i].Index));
            Assert.That(actual.Slots[i].Value, Is.EqualTo(expected.Slots[i].Value));
        }
    }
}
