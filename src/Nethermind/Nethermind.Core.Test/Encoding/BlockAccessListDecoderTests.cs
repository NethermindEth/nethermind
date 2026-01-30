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
        // Note: UInt256 constructor from bytes needs isBigEndian: true to match RLP encoding
        StorageChange parentHashStorageChange = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));
        SlotChanges expected = new(0, new SortedList<ushort, StorageChange> { { 0, parentHashStorageChange } });

        // Generate expected RLP from the object (uses variable-length encoding per EIP-7928)
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
        // Create expected StorageChange with a large UInt256 value
        // Note: UInt256 constructor from bytes uses little-endian, but we want the value 0xc382836f...
        // which when RLP encoded gives the same hex string as the value bytes
        StorageChange expected = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));

        // Generate expected RLP from the object (uses variable-length encoding per EIP-7928)
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

        var storageChanges = new SortedList<ushort, StorageChange> { { 10, storageChange }, { 10, storageChange } };
        SlotChanges slotChanges = new(0xbad, storageChanges);
        byte[] slotChangesBytes = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;
        SlotChanges slotChangesDecoded = Rlp.Decode<SlotChanges>(slotChangesBytes, RlpBehaviors.None);
        Assert.That(slotChanges, Is.EqualTo(slotChangesDecoded));

        StorageRead storageRead = new(0xbababa);
        StorageRead storageRead2 = new(0xcacaca);
        byte[] storageReadBytes = Rlp.Encode(storageRead, RlpBehaviors.None).Bytes;
        StorageRead storageReadDecoded = Rlp.Decode<StorageRead>(storageReadBytes, RlpBehaviors.None);
        Assert.That(storageRead, Is.EqualTo(storageReadDecoded));

        BalanceChange balanceChange = new()
        {
            BlockAccessIndex = 10,
            PostBalance = 0
        };
        BalanceChange balanceChange2 = new()
        {
            BlockAccessIndex = 11,
            PostBalance = 1
        };
        byte[] balanceChangeBytes = Rlp.Encode(balanceChange, RlpBehaviors.None).Bytes;
        BalanceChange balanceChangeDecoded = Rlp.Decode<BalanceChange>(balanceChangeBytes, RlpBehaviors.None);
        Assert.That(balanceChange, Is.EqualTo(balanceChangeDecoded));

        NonceChange nonceChange = new()
        {
            BlockAccessIndex = 10,
            NewNonce = 0
        };
        NonceChange nonceChange2 = new()
        {
            BlockAccessIndex = 11,
            NewNonce = 0
        };
        byte[] nonceChangeBytes = Rlp.Encode(nonceChange, RlpBehaviors.None).Bytes;
        NonceChange nonceChangeDecoded = Rlp.Decode<NonceChange>(nonceChangeBytes, RlpBehaviors.None);
        Assert.That(nonceChange, Is.EqualTo(nonceChangeDecoded));

        CodeChange codeChange = new()
        {
            BlockAccessIndex = 10,
            NewCode = [0, 50]
        };
        byte[] codeChangeBytes = Rlp.Encode(codeChange, RlpBehaviors.None).Bytes;
        CodeChange codeChangeDecoded = Rlp.Decode<CodeChange>(codeChangeBytes, RlpBehaviors.None);
        Assert.That(codeChange, Is.EqualTo(codeChangeDecoded));

        SortedDictionary<UInt256, SlotChanges> storageChangesDict = new()
        {
            { slotChanges.Slot, slotChanges }
        };

        SortedList<ushort, BalanceChange> balanceChangesList = new()
        {
            { balanceChange.BlockAccessIndex, balanceChange },
            { balanceChange2.BlockAccessIndex, balanceChange2 }
        };

        SortedList<ushort, NonceChange> nonceChangesList = new()
        {
            { nonceChange.BlockAccessIndex, nonceChange },
            { nonceChange2.BlockAccessIndex, nonceChange2 }
        };

        SortedList<ushort, CodeChange> codeChangesList = new()
        {
            { codeChange.BlockAccessIndex, codeChange },
        };

        AccountChanges accountChanges = new(
            TestItem.AddressA,
            storageChangesDict,
            [storageRead, storageRead2],
            balanceChangesList,
            nonceChangesList,
            codeChangesList
        );
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
            UInt256 eip4788Slot1 = 0xc;
            // Note: UInt256 constructor from bytes needs isBigEndian: true to match RLP encoding
            StorageChange parentHashStorageChange = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));
            // Note: value 0x0c (12) encoded as variable-length UInt256, not 32 bytes
            StorageChange timestampStorageChange = new(0, 0xc);
            SortedDictionary<Address, AccountChanges> expectedAccountChanges = new()
            {
                {Eip7002Constants.WithdrawalRequestPredeployAddress, new(
                    Eip7002Constants.WithdrawalRequestPredeployAddress,
                    [],
                    [new(0), new(1), new(2), new(3)],
                    [],
                    [],
                    []
                )},
                {Eip7251Constants.ConsolidationRequestPredeployAddress, new(
                    Eip7251Constants.ConsolidationRequestPredeployAddress,
                    [],
                    [new(0), new(1), new(2), new(3)],
                    [],
                    [],
                    []
                )},
                {Eip2935Constants.BlockHashHistoryAddress, new(
                    Eip2935Constants.BlockHashHistoryAddress,
                    new SortedDictionary<UInt256, SlotChanges>() { { 0, new SlotChanges(0, new SortedList<ushort, StorageChange>{{0, parentHashStorageChange}}) } },
                    [],
                    [],
                    [],
                    []
                )},
                {Eip4788Constants.BeaconRootsAddress, new(
                    Eip4788Constants.BeaconRootsAddress,
                    new SortedDictionary<UInt256, SlotChanges>() { { eip4788Slot1, new SlotChanges(eip4788Slot1, new SortedList<ushort, StorageChange>{{0, timestampStorageChange}}) } },
                    [new(0x200b)],
                    [],
                    [],
                    []
                )},
                {new("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba"), new(
                    new("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba"),
                    [],
                    [],
                    new SortedList<ushort, BalanceChange> { { 1, new(1, 0x1319718811c8) } },
                    [],
                    []
                )},
                {new("0xaccc7d92b051544a255b8a899071040739bada75"), new(
                    new("0xaccc7d92b051544a255b8a899071040739bada75"),
                    [],
                    [],
                    new SortedList<ushort, BalanceChange> { { 1, new(1, new UInt256(Bytes.FromHexString("0x3635c99aac6d15af9c"), isBigEndian: true)) } },
                    new SortedList<ushort, NonceChange> { { 1, new(1, 1) } },
                    []
                )},
                {new("0xd9c0e57d447779673b236c7423aeab84e931f3ba"), new(
                    new("0xd9c0e57d447779673b236c7423aeab84e931f3ba"),
                    [],
                    [],
                    new SortedList<ushort, BalanceChange> { { 1, new(1, 0x64) } },
                    [],
                    []
                )},
            };
            BlockAccessList expected = new(expectedAccountChanges);
            // Generate RLP from object (uses variable-length encoding per EIP-7928)
            string balanceChangesRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);
            yield return new TestCaseData(balanceChangesRlp, expected)
            { TestName = "balance_changes" };
        }
    }

    private static IEnumerable<TestCaseData> AccountChangesTestSource
    {
        get
        {
            AccountChanges storageReadsExpected = new(
                Eip7002Constants.WithdrawalRequestPredeployAddress,
                [],
                [new(0), new(1), new(2), new(3)],
                [],
                [],
                []
            );
            // Generate RLP from object (uses variable-length encoding per EIP-7928)
            string storageReadsRlp = "0x" + Bytes.ToHexString(Rlp.Encode(storageReadsExpected).Bytes);
            yield return new TestCaseData(storageReadsRlp, storageReadsExpected)
            { TestName = "storage_reads" };

            AccountChanges storageChangesExpected = new(
                Eip2935Constants.BlockHashHistoryAddress,
                new SortedDictionary<UInt256, SlotChanges>() { { 0, new SlotChanges(0, new SortedList<ushort, StorageChange> { { 0, new(0, new(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true)) } }) } },
                [],
                [],
                [],
                []
            );
            // Generate RLP from object (uses variable-length encoding per EIP-7928)
            string storageChangesRlp = "0x" + Bytes.ToHexString(Rlp.Encode(storageChangesExpected).Bytes);
            yield return new TestCaseData(storageChangesRlp, storageChangesExpected)
            { TestName = "storage_changes" };
        }
    }
}
