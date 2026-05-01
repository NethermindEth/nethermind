// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public class BlockAccessListDecoderTests
{
    [TestCaseSource(nameof(BlockAccessListTestSource))]
    public void Can_decode_then_encode(string rlp, ReadOnlyBlockAccessList expected)
    {
        ReadOnlyBlockAccessList bal = Rlp.Decode<ReadOnlyBlockAccessList>(Bytes.FromHexString(rlp));

        Assert.That(bal, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(bal).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_decode_then_encode_balance_change()
    {
        const string rlp = "0xc801861319718811c8";
        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(rlp));
        BalanceChange balanceChange = BalanceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        BalanceChange expected = new(1, 0x1319718811c8);
        Assert.That(balanceChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(balanceChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_decode_then_encode_nonce_change()
    {
        const string rlp = "0xc20101";
        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(rlp));
        NonceChange nonceChange = NonceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        NonceChange expected = new(1, 1);
        Assert.That(nonceChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(nonceChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_decode_then_encode_slot_change()
    {
        StorageChange parentHashStorageChange = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));
        ReadOnlySlotChanges expected = new(0, [parentHashStorageChange]);

        string expectedRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);

        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(expectedRlp));
        ReadOnlySlotChanges slotChange = SlotChangesDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        Assert.That(slotChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(slotChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(expectedRlp);
        Assert.That(encoded, Is.EqualTo(expectedRlp));
    }

    [Test]
    public void Can_decode_then_encode_storage_change()
    {
        StorageChange expected = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));

        string expectedRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);

        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(expectedRlp));
        StorageChange storageChange = StorageChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        Assert.That(storageChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(storageChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(expectedRlp);
        Assert.That(encoded, Is.EqualTo(expectedRlp));
    }

    [Test]
    public void Can_decode_then_encode_code_change()
    {
        const string rlp = "0xc20100";

        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(rlp));
        CodeChange codeChange = CodeChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        CodeChange expected = new(1, [0x0]);
        Assert.That(codeChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(codeChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [TestCaseSource(nameof(AccountChangesTestSource))]
    public void Can_decode_then_encode_account_change(string rlp, ReadOnlyAccountChanges expected)
    {
        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(rlp));
        ReadOnlyAccountChanges accountChange = AccountChangesDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);

        Assert.That(accountChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(accountChange).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_encode_then_decode()
    {
        StorageChange storageChange = new()
        {
            Index = 10,
            Value = 0xcad
        };
        byte[] storageChangeBytes = Rlp.Encode(storageChange, RlpBehaviors.None).Bytes;
        StorageChange storageChangeDecoded = Rlp.Decode<StorageChange>(storageChangeBytes, RlpBehaviors.None);
        Assert.That(storageChange, Is.EqualTo(storageChangeDecoded));

        StorageChange[] storageChanges = [storageChange];
        ReadOnlySlotChanges slotChanges = new(0xbad, storageChanges);
        byte[] slotChangesBytes = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;
        ReadOnlySlotChanges slotChangesDecoded = Rlp.Decode<ReadOnlySlotChanges>(slotChangesBytes, RlpBehaviors.None);
        Assert.That(slotChanges, Is.EqualTo(slotChangesDecoded));

        UInt256 storageRead = 0xbababa;
        byte[] storageReadBytes = Rlp.Encode(storageRead).Bytes;
        UInt256 storageReadDecoded = Rlp.Decode<UInt256>(storageReadBytes, RlpBehaviors.None);
        Assert.That(storageRead, Is.EqualTo(storageReadDecoded));

        BalanceChange balanceChange = new(10, 0);
        BalanceChange balanceChange2 = new(11, 1);
        byte[] balanceChangeBytes = Rlp.Encode(balanceChange, RlpBehaviors.None).Bytes;
        BalanceChange balanceChangeDecoded = Rlp.Decode<BalanceChange>(balanceChangeBytes, RlpBehaviors.None);
        Assert.That(balanceChange, Is.EqualTo(balanceChangeDecoded));

        NonceChange nonceChange = new(10, 0);
        NonceChange nonceChange2 = new(11, 0);
        byte[] nonceChangeBytes = Rlp.Encode(nonceChange, RlpBehaviors.None).Bytes;
        NonceChange nonceChangeDecoded = Rlp.Decode<NonceChange>(nonceChangeBytes, RlpBehaviors.None);
        Assert.That(nonceChange, Is.EqualTo(nonceChangeDecoded));

        CodeChange codeChange = new(10, [0, 50]);
        byte[] codeChangeBytes = Rlp.Encode(codeChange, RlpBehaviors.None).Bytes;
        CodeChange codeChangeDecoded = Rlp.Decode<CodeChange>(codeChangeBytes, RlpBehaviors.None);
        Assert.That(codeChange, Is.EqualTo(codeChangeDecoded));

        ReadOnlyAccountChanges accountChanges = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(slotChanges.Key, storageChange)
            .WithStorageReads(0xbababa, 0xcacaca)
            .WithBalanceChanges([balanceChange, balanceChange2])
            .WithNonceChanges([nonceChange, nonceChange2])
            .WithCodeChanges(codeChange)
            .TestObject;
        byte[] accountChangesBytes = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;
        ReadOnlyAccountChanges accountChangesDecoded = Rlp.Decode<ReadOnlyAccountChanges>(accountChangesBytes, RlpBehaviors.None);
        Assert.That(accountChanges, Is.EqualTo(accountChangesDecoded));

        ReadOnlyBlockAccessList blockAccessList = Build.A.BlockAccessList.WithAccountChanges(accountChanges).TestObject;
        byte[] blockAccessListBytes = Rlp.Encode(blockAccessList, RlpBehaviors.None).Bytes;
        ReadOnlyBlockAccessList blockAccessListDecoded = Rlp.Decode<ReadOnlyBlockAccessList>(blockAccessListBytes, RlpBehaviors.None);
        Assert.That(blockAccessList, Is.EqualTo(blockAccessListDecoded));
    }

    [Test]
    public void Decoding_block_access_list_with_unsorted_account_changes_throws()
    {
        // Use explicit, lexicographically-ordered addresses (low < high) — TestItem.Address* are
        // derived from public keys and don't have a predictable ordering.
        Address low = new("0x0000000000000000000000000000000000000001");
        Address high = new("0x0000000000000000000000000000000000000002");

        ReadOnlyAccountChanges accountChangesLow = Build.An.AccountChanges.WithAddress(low).TestObject;
        ReadOnlyAccountChanges accountChangesHigh = Build.An.AccountChanges.WithAddress(high).TestObject;
        // produce an out-of-order encoding by hand: high before low
        ReadOnlyBlockAccessList badBal = new([accountChangesHigh, accountChangesLow], 2);
        byte[] encoded = Rlp.Encode(badBal, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<ReadOnlyBlockAccessList>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Account changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_storage_changes_throws()
    {
        UInt256 slot1 = UInt256.One;
        UInt256 slot2 = new(2);
        // Pass slot changes in descending order so encoding emits unsorted RLP.
        ReadOnlySlotChanges[] storageChanges = [new ReadOnlySlotChanges(slot2), new ReadOnlySlotChanges(slot1)];
        ReadOnlyAccountChanges accountChanges = new(
            TestItem.AddressA,
            storageChanges,
            [],
            [],
            [],
            []);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<ReadOnlyAccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Storage changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_storage_reads_throws()
    {
        UInt256[] storageReads = [new UInt256(2), UInt256.One];
        ReadOnlyAccountChanges accountChanges = new(
            TestItem.AddressA,
            [],
            storageReads,
            [],
            [],
            []);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<ReadOnlyAccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Storage reads were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_balance_changes_throws()
    {
        BalanceChange[] balanceChanges = [new(2, UInt256.Zero), new(1, UInt256.One)];
        ReadOnlyAccountChanges accountChanges = new(
            TestItem.AddressA,
            [],
            [],
            balanceChanges,
            [],
            []);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<ReadOnlyAccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Balance changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_nonce_changes_throws()
    {
        NonceChange[] nonceChanges = [new(2, 2), new(1, 1)];
        ReadOnlyAccountChanges accountChanges = new(
            TestItem.AddressA,
            [],
            [],
            [],
            nonceChanges,
            []);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<ReadOnlyAccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Nonce changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_code_changes_throws()
    {
        CodeChange[] codeChanges = [new(2, [0x02]), new(1, [0x01])];
        ReadOnlyAccountChanges accountChanges = new(
            TestItem.AddressA,
            [],
            [],
            [],
            [],
            codeChanges);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<ReadOnlyAccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Code changes were in incorrect order."));
    }

    [Test]
    public void Decoding_slot_changes_with_unsorted_storage_changes_throws()
    {
        StorageChange[] storageChanges = [new(2, UInt256.Zero), new(1, UInt256.One)];
        ReadOnlySlotChanges slotChanges = new(UInt256.One, storageChanges);
        byte[] encoded = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<ReadOnlySlotChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Storage changes were in incorrect order. index=1, lastIndex=2"));
    }

    private static IEnumerable<TestCaseData> BlockAccessListTestSource
    {
        get
        {
            StorageChange parentHashStorageChange = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));
            StorageChange timestampStorageChange = new(0, 0xc);

            ReadOnlyBlockAccessList expected = Build.A.BlockAccessList
                .WithAccountChanges(
                    Build.An.AccountChanges
                        .WithAddress(Eip7002Constants.WithdrawalRequestPredeployAddress)
                        .WithStorageReads(0, 1, 2, 3)
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(Eip7251Constants.ConsolidationRequestPredeployAddress)
                        .WithStorageReads(0, 1, 2, 3)
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(Eip2935Constants.BlockHashHistoryAddress)
                        .WithStorageChanges(0, parentHashStorageChange)
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(Eip4788Constants.BeaconRootsAddress)
                        .WithStorageChanges(0xc, timestampStorageChange)
                        .WithStorageReads(0x200b)
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(new("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba"))
                        .WithBalanceChanges([new(1, 0x1319718811c8)])
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(new("0xaccc7d92b051544a255b8a899071040739bada75"))
                        .WithBalanceChanges([new(1, new UInt256(Bytes.FromHexString("0x3635c99aac6d15af9c"), isBigEndian: true))])
                        .WithNonceChanges([new(1, 1)])
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(new("0xd9c0e57d447779673b236c7423aeab84e931f3ba"))
                        .WithBalanceChanges([new(1, 0x64)])
                        .TestObject)
                .TestObject;
            string balanceChangesRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);
            yield return new TestCaseData(balanceChangesRlp, expected) { TestName = "balance_changes" };
        }
    }

    private static IEnumerable<TestCaseData> AccountChangesTestSource
    {
        get
        {
            ReadOnlyAccountChanges storageReadsExpected = Build.An.AccountChanges
                .WithAddress(Eip7002Constants.WithdrawalRequestPredeployAddress)
                .WithStorageReads(0, 1, 2, 3)
                .TestObject;
            string storageReadsRlp = "0x" + Bytes.ToHexString(Rlp.Encode(storageReadsExpected).Bytes);
            yield return new TestCaseData(storageReadsRlp, storageReadsExpected) { TestName = "storage_reads" };

            ReadOnlyAccountChanges storageChangesExpected = Build.An.AccountChanges
                .WithAddress(Eip2935Constants.BlockHashHistoryAddress)
                .WithStorageChanges(
                    0,
                    [new(0, new(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true))])
                .TestObject;
            string storageChangesRlp = "0x" + Bytes.ToHexString(Rlp.Encode(storageChangesExpected).Bytes);
            yield return new TestCaseData(storageChangesRlp, storageChangesExpected) { TestName = "storage_changes" };
        }
    }

}
