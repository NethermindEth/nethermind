// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
    public void Can_decode_then_encode(string rlp, BlockAccessList expected)
    {
        BlockAccessList bal = Rlp.Decode<BlockAccessList>(Bytes.FromHexString(rlp).AsRlpStream());

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
        SlotChanges expected = new(0, new SortedList<ushort, StorageChange> { { 0, parentHashStorageChange } });

        string expectedRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);

        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(expectedRlp));
        SlotChanges slotChange = SlotChangesDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
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
    public void Can_decode_then_encode_account_change(string rlp, AccountChanges expected)
    {
        Rlp.ValueDecoderContext ctx = new(Bytes.FromHexString(rlp));
        AccountChanges accountChange = AccountChangesDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);

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
            BlockAccessIndex = 10,
            NewValue = 0xcad
        };
        byte[] storageChangeBytes = Rlp.Encode(storageChange, RlpBehaviors.None).Bytes;
        StorageChange storageChangeDecoded = Rlp.Decode<StorageChange>(storageChangeBytes, RlpBehaviors.None);
        Assert.That(storageChange, Is.EqualTo(storageChangeDecoded));

        var storageChanges = new SortedList<ushort, StorageChange> { { 10, storageChange }};
        SlotChanges slotChanges = new(0xbad, storageChanges);
        byte[] slotChangesBytes = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;
        SlotChanges slotChangesDecoded = Rlp.Decode<SlotChanges>(slotChangesBytes, RlpBehaviors.None);
        Assert.That(slotChanges, Is.EqualTo(slotChangesDecoded));

        StorageRead storageRead = new(0xbababa);
        StorageRead storageRead2 = new(0xcacaca);
        byte[] storageReadBytes = Rlp.Encode(storageRead, RlpBehaviors.None).Bytes;
        StorageRead storageReadDecoded = Rlp.Decode<StorageRead>(storageReadBytes, RlpBehaviors.None);
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

        AccountChanges accountChanges = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(slotChanges.Slot, storageChange)
            .WithStorageReads(0xbababa, 0xcacaca)
            .WithBalanceChanges([balanceChange, balanceChange2])
            .WithNonceChanges([nonceChange, nonceChange2])
            .WithCodeChanges(codeChange)
            .TestObject;
        byte[] accountChangesBytes = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;
        AccountChanges accountChangesDecoded = Rlp.Decode<AccountChanges>(accountChangesBytes, RlpBehaviors.None);
        Assert.That(accountChanges, Is.EqualTo(accountChangesDecoded));

        SortedDictionary<Address, AccountChanges> accountChangesDict = new()
        {
            { accountChanges.Address, accountChanges }
        };

        BlockAccessList blockAccessList = new(accountChangesDict);
        byte[] blockAccessListBytes = Rlp.Encode(blockAccessList, RlpBehaviors.None).Bytes;
        BlockAccessList blockAccessListDecoded = Rlp.Decode<BlockAccessList>(blockAccessListBytes, RlpBehaviors.None);
        Assert.That(blockAccessList, Is.EqualTo(blockAccessListDecoded));
    }

    private static IEnumerable<TestCaseData> BlockAccessListTestSource
    {
        get
        {
            StorageChange parentHashStorageChange = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));
            StorageChange timestampStorageChange = new(0, 0xc);
            SortedDictionary<Address, AccountChanges> expectedAccountChanges = new()
            {
                {
                    Eip7002Constants.WithdrawalRequestPredeployAddress,
                    Build.An.AccountChanges
                        .WithAddress(Eip7002Constants.WithdrawalRequestPredeployAddress)
                        .WithStorageReads(0, 1, 2, 3)
                        .TestObject
                },
                {
                    Eip7251Constants.ConsolidationRequestPredeployAddress,
                    Build.An.AccountChanges
                        .WithAddress(Eip7251Constants.ConsolidationRequestPredeployAddress)
                        .WithStorageReads(0, 1, 2, 3)
                        .TestObject
                },
                {
                    Eip2935Constants.BlockHashHistoryAddress,
                    Build.An.AccountChanges
                        .WithAddress(Eip2935Constants.BlockHashHistoryAddress)
                        .WithStorageChanges(0, parentHashStorageChange)
                        .TestObject
                },
                {
                    Eip4788Constants.BeaconRootsAddress,
                    Build.An.AccountChanges
                        .WithAddress(Eip4788Constants.BeaconRootsAddress)
                        .WithStorageChanges(0xc, timestampStorageChange)
                        .WithStorageReads(0x200b)
                        .TestObject
                },
                {
                    new("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba"),
                    Build.An.AccountChanges
                        .WithAddress(new("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba"))
                        .WithBalanceChanges([new(1, 0x1319718811c8)])
                        .TestObject
                },
                {
                    new("0xaccc7d92b051544a255b8a899071040739bada75"),
                    Build.An.AccountChanges
                        .WithAddress(new("0xaccc7d92b051544a255b8a899071040739bada75"))
                        .WithBalanceChanges([new(1, new UInt256(Bytes.FromHexString("0x3635c99aac6d15af9c"), isBigEndian: true))])
                        .WithNonceChanges([new(1, 1)])
                        .TestObject
                },
                {
                    new("0xd9c0e57d447779673b236c7423aeab84e931f3ba"),
                    Build.An.AccountChanges
                        .WithAddress(new("0xd9c0e57d447779673b236c7423aeab84e931f3ba"))
                        .WithBalanceChanges([new(1, 0x64)])
                        .TestObject
                },
            };
            BlockAccessList expected = new(expectedAccountChanges);
            string balanceChangesRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);
            yield return new TestCaseData(balanceChangesRlp, expected)
            { TestName = "balance_changes" };
        }
    }

    private static IEnumerable<TestCaseData> AccountChangesTestSource
    {
        get
        {
            AccountChanges storageReadsExpected = Build.An.AccountChanges
                .WithAddress(Eip7002Constants.WithdrawalRequestPredeployAddress)
                .WithStorageReads(0, 1, 2, 3)
                .TestObject;
            string storageReadsRlp = "0x" + Bytes.ToHexString(Rlp.Encode(storageReadsExpected).Bytes);
            yield return new TestCaseData(storageReadsRlp, storageReadsExpected)
            { TestName = "storage_reads" };

            AccountChanges storageChangesExpected = Build.An.AccountChanges
                .WithAddress(Eip2935Constants.BlockHashHistoryAddress)
                .WithStorageChanges(
                    0,
                    [new(0, new(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true))])
                .TestObject;
            string storageChangesRlp = "0x" + Bytes.ToHexString(Rlp.Encode(storageChangesExpected).Bytes);
            yield return new TestCaseData(storageChangesRlp, storageChangesExpected)
            { TestName = "storage_changes" };
        }
    }
}
