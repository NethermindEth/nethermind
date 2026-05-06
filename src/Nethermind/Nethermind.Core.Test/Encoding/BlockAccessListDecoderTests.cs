// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
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
        BlockAccessList bal = Rlp.Decode<BlockAccessList>(Bytes.FromHexString(rlp));

        Assert.That(bal, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(bal).Bytes);
        Console.WriteLine(encoded);
        Console.WriteLine(rlp);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    // Truncated RLP causes an out-of-bounds primitive read; the Rlp.Decode entry-point
    // wrap converts that to RlpException so callers see a consistent failure mode
    // (engine_newPayloadV5 returns a clean error instead of crashing the RPC).
    [Test]
    public void Decode_empty_bytes_throws_RlpException() =>
        Assert.That(() => Rlp.Decode<BlockAccessList>([]), Throws.TypeOf<RlpException>());

    // 0xf8 announces a long-form list with a 1-byte length follower, but the byte is missing.
    [Test]
    public void Decode_truncated_outer_list_throws_RlpException() =>
        Assert.That(() => Rlp.Decode<BlockAccessList>(new byte[] { 0xf8 }), Throws.TypeOf<RlpException>());

    // 0xc1 0xc0 = outer list of 1 containing an empty inner list. EIP-7928 requires each
    // AccountChanges to be a 6-field sequence; an empty list is structurally invalid.
    [Test]
    public void Decode_inner_empty_list_in_account_changes_throws_RlpException() =>
        Assert.That(() => Rlp.Decode<BlockAccessList>(new byte[] { 0xc1, 0xc0 }), Throws.TypeOf<RlpException>());

    [Test]
    public void Decode_account_changes_without_changes_or_reads_throws_RlpException()
    {
        SortedDictionary<Address, AccountChanges> accountChanges = new()
        {
            { TestItem.AddressA, new AccountChanges(TestItem.AddressA) }
        };
        BlockAccessList blockAccessList = new(accountChanges);
        byte[] encoded = Rlp.Encode(blockAccessList).Bytes;

        Assert.That(
            () => Rlp.Decode<BlockAccessList>(encoded),
            Throws.TypeOf<RlpException>().With.Message.Contain("has no changes or reads"));
    }

    [Test]
    public void DecodeArrayPool_disposes_partial_list_when_element_decoder_throws()
    {
        TextWriter originalError = Console.Error;
        using StringWriter error = new();
        Console.SetError(error);
        try
        {
            Rlp.ValueDecoderContext ctx = new(new byte[] { 0xc2, 0x01, 0x02 });

            RlpException? exception = null;
            try
            {
                Rlp.DecodeArrayPool(ref ctx, new ThrowingByteDecoder());
            }
            catch (RlpException e)
            {
                exception = e;
            }

            Assert.That(exception?.Message, Is.EqualTo(ThrowingByteDecoder.Error));

            GC.Collect();
            GC.WaitForPendingFinalizers();

            Assert.That(error.ToString(), Does.Not.Contain(nameof(ArrayPoolList<byte>)));
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Test]
    public void Decode_slot_changes_with_empty_accesses_throws_RlpException()
    {
        // SlotChanges = [StorageKey, List[StorageChange]]. An empty StorageChange list means a
        // slot with no changes — that slot belongs in storage_reads instead. Geth bal-devnet-4
        // rejects this with "empty storage writes".
        SlotChanges withEmptyChanges = new(123u, new SortedList<uint, StorageChange>(PrestateAwareIndexComparer.Instance));
        byte[] rlp = Rlp.Encode(withEmptyChanges).Bytes;

        RlpException? thrown = null;
        try
        {
            Rlp.ValueDecoderContext ctx = new(rlp);
            SlotChangesDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        }
        catch (RlpException e)
        {
            thrown = e;
        }

        Assert.That(thrown, Is.Not.Null);
        Assert.That(thrown!.Message, Does.Contain("Empty storage_changes"));
    }

    [Test]
    public void Decoded_slot_changes_uses_prestate_aware_comparer()
    {
        // Wire path: AccountChangesDecoder/SlotChangesDecoder build SortedLists with
        // PrestateAwareIndexComparer so that LoadPreStateToSuggestedBlockAccessList grafting
        // a PrestateIndex entry afterwards keeps it sorted first. Decoded entries are real
        // ascending uints, behaviorally identical to plain ascending — but the comparer must
        // be the prestate-aware one for the later graft to behave correctly.
        StorageChange change = new(0, 0xCC);
        SlotChanges seed = new(7u, new SortedList<uint, StorageChange>(PrestateAwareIndexComparer.Instance) { { 0, change } });
        byte[] rlp = Rlp.Encode(seed).Bytes;

        Rlp.ValueDecoderContext ctx = new(rlp);
        SlotChanges decoded = SlotChangesDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);

        // Graft a prestate entry as LoadPreState would, then verify it lands first.
        decoded.AddStorageChange(new StorageChange(Eip7928Constants.PrestateIndex, 0xAA));
        Assert.That(decoded.Changes.Keys[0], Is.EqualTo(Eip7928Constants.PrestateIndex));
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
    public void Balance_change_roundtrips_with_index_above_uint16_range()
    {
        // EIP-7928 widened BlockAccessIndex to uint32 (commit 645099785a). This test catches
        // any regression to the old uint16 decoder by using an index above 65535.
        BalanceChange original = new(0x10_0000u, 0x42);

        Rlp encoded = Rlp.Encode(original);
        Rlp.ValueDecoderContext ctx = new(encoded.Bytes);
        BalanceChange decoded = BalanceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);

        Assert.That(decoded, Is.EqualTo(original));
        Assert.That(decoded.Index, Is.EqualTo(0x10_0000u));
    }

    [Test]
    public void Balance_change_roundtrips_with_max_uint32_index()
    {
        // Upper bound of EIP-7928 BlockAccessIndex spec.
        // (Eip7928Constants.PrestateIndex collides with this value but is internal-only and
        // never appears on the wire.)
        BalanceChange original = new(uint.MaxValue, 0x1);

        Rlp encoded = Rlp.Encode(original);
        Rlp.ValueDecoderContext ctx = new(encoded.Bytes);
        BalanceChange decoded = BalanceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);

        Assert.That(decoded.Index, Is.EqualTo(uint.MaxValue));
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
        SlotChanges expected = new(0, new SortedList<uint, StorageChange> { { 0, parentHashStorageChange } });

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
            Index = 10,
            Value = 0xcad
        };
        byte[] storageChangeBytes = Rlp.Encode(storageChange, RlpBehaviors.None).Bytes;
        StorageChange storageChangeDecoded = Rlp.Decode<StorageChange>(storageChangeBytes, RlpBehaviors.None);
        Assert.That(storageChange, Is.EqualTo(storageChangeDecoded));

        SortedList<uint, StorageChange> storageChanges = new() { { 10, storageChange } };
        SlotChanges slotChanges = new(0xbad, storageChanges);
        byte[] slotChangesBytes = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;
        SlotChanges slotChangesDecoded = Rlp.Decode<SlotChanges>(slotChangesBytes, RlpBehaviors.None);
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

        AccountChanges accountChanges = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(slotChanges.Key, storageChange)
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

    [Test]
    public void Decoding_block_access_list_with_unsorted_account_changes_throws()
    {
        AccountChanges accountChangesA = Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject;
        AccountChanges accountChangesB = Build.An.AccountChanges.WithAddress(TestItem.AddressB).TestObject;
        SortedDictionary<Address, AccountChanges> accountChanges = new(DescendingComparer<Address>())
        {
            { accountChangesA.Address, accountChangesA },
            { accountChangesB.Address, accountChangesB }
        };

        BlockAccessList blockAccessList = new(accountChanges);
        byte[] encoded = Rlp.Encode(blockAccessList, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<BlockAccessList>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Account changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_storage_changes_throws()
    {
        UInt256 slot1 = UInt256.One;
        UInt256 slot2 = new(2);
        // Each SlotChanges must have at least one StorageChange (per EIP-7928 / geth bal-devnet-4
        // "empty storage_changes" rejection). Add a real change so this test exercises the
        // unsorted-slot-order check rather than the empty-changes check.
        SortedList<uint, StorageChange> innerChanges1 = new(PrestateAwareIndexComparer.Instance) { { 0, new StorageChange(0, 1) } };
        SortedList<uint, StorageChange> innerChanges2 = new(PrestateAwareIndexComparer.Instance) { { 0, new StorageChange(0, 2) } };
        SortedList<UInt256, SlotChanges> storageChanges = new(DescendingComparer<UInt256>())
        {
            { slot1, new SlotChanges(slot1, innerChanges1) },
            { slot2, new SlotChanges(slot2, innerChanges2) }
        };
        AccountChanges accountChanges = new(
            TestItem.AddressA,
            storageChanges,
            [],
            [],
            [],
            []);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Storage changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_storage_reads_throws()
    {
        SortedSet<UInt256> storageReads = new(DescendingComparer<UInt256>())
        {
            UInt256.One,
            new UInt256(2)
        };
        AccountChanges accountChanges = new(
            TestItem.AddressA,
            [],
            storageReads,
            [],
            [],
            []);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Storage reads were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_balance_changes_throws()
    {
        SortedList<uint, BalanceChange> balanceChanges = new(DescendingComparer<uint>())
        {
            { 1, new(1, UInt256.One) },
            { 2, new(2, UInt256.Zero) }
        };
        AccountChanges accountChanges = new(
            TestItem.AddressA,
            [],
            [],
            balanceChanges,
            [],
            []);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Balance changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_nonce_changes_throws()
    {
        SortedList<uint, NonceChange> nonceChanges = new(DescendingComparer<uint>())
        {
            { 1, new(1, 1) },
            { 2, new(2, 2) }
        };
        AccountChanges accountChanges = new(
            TestItem.AddressA,
            [],
            [],
            [],
            nonceChanges,
            []);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Nonce changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_code_changes_throws()
    {
        SortedList<uint, CodeChange> codeChanges = new(DescendingComparer<uint>())
        {
            { 1, new(1, [0x01]) },
            { 2, new(2, [0x02]) }
        };
        AccountChanges accountChanges = new(
            TestItem.AddressA,
            [],
            [],
            [],
            [],
            codeChanges);

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Code changes were in incorrect order."));
    }

    [Test]
    public void Decoding_slot_changes_with_unsorted_storage_changes_throws()
    {
        SortedList<uint, StorageChange> storageChanges = new(DescendingComparer<uint>())
        {
            { 1, new(1, UInt256.One) },
            { 2, new(2, UInt256.Zero) }
        };
        SlotChanges slotChanges = new(UInt256.One, storageChanges);
        byte[] encoded = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;

        Assert.That(
            () => Rlp.Decode<SlotChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Storage changes were in incorrect order. index=1, lastIndex=2"));
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

    private static IComparer<T> DescendingComparer<T>() where T : IComparable<T>
        => Comparer<T>.Create((left, right) => right.CompareTo(left));

    private sealed class ThrowingByteDecoder : IRlpValueDecoder<byte>
    {
        public const string Error = "semantic failure";
        private int _calls;

        public byte Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            byte value = decoderContext.DecodeByte();
            _calls++;
            if (_calls == 2)
            {
                throw new RlpException(Error);
            }

            return value;
        }

        public int GetLength(byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => 1;
    }
}
