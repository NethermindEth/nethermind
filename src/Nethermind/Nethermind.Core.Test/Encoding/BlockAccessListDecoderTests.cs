// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    // 0x80 is an empty byte string, not an EIP-7928 BAL list. The public Decode<T>
    // entry point must fail with a typed RlpException rather than returning null.
    [Test]
    public void Decode_empty_string_throws_RlpException() =>
        Assert.That(() => Rlp.Decode<BlockAccessList>(new byte[] { 0x80 }), Throws.TypeOf<RlpException>());

    // 0xc1 0xc0 = outer list of 1 containing an empty inner list. EIP-7928 requires each
    // AccountChanges to be a 6-field sequence; an empty list is structurally invalid.
    [Test]
    public void Decode_inner_empty_list_in_account_changes_throws_RlpException() =>
        Assert.That(() => Rlp.Decode<BlockAccessList>(new byte[] { 0xc1, 0xc0 }), Throws.TypeOf<RlpException>());

    [Test]
    public void Decode_empty_slot_changes_entry_in_account_changes_throws_RlpException()
    {
        byte[] encoded = EncodeAccountChangesWithEmptySlotChangesEntry(TestItem.AddressA);

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Empty SlotChanges entry; EIP-7928 requires a 2-field sequence."));
    }

    [Test]
    public void Decode_account_changes_without_changes_or_reads_roundtrips_as_account_read()
    {
        SortedDictionary<Address, AccountChanges> accountChanges = new()
        {
            { TestItem.AddressA, new AccountChanges(TestItem.AddressA) }
        };
        BlockAccessList blockAccessList = new(accountChanges);
        byte[] encoded = Rlp.Encode(blockAccessList).Bytes;

        BlockAccessList decoded = Rlp.Decode<BlockAccessList>(encoded);

        Assert.That(decoded, Is.EqualTo(blockAccessList));
        Assert.That(decoded.ItemCount, Is.EqualTo(1));
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
    public void DecodeArrayPool_disposes_decoded_items_when_element_decoder_throws()
    {
        DisposableElement.DisposedCount = 0;
        Rlp.ValueDecoderContext ctx = new(new byte[] { 0xc2, 0x01, 0x02 });

        RlpException? exception = null;
        try
        {
            Rlp.DecodeArrayPool(ref ctx, new ThrowingDisposableDecoder());
        }
        catch (RlpException e)
        {
            exception = e;
        }

        Assert.That(exception?.Message, Is.EqualTo(ThrowingDisposableDecoder.Error));
        Assert.That(DisposableElement.DisposedCount, Is.EqualTo(1));
    }

    [Test]
    public void DecodeArrayPool_disposes_runtime_disposable_items_when_static_type_does_not_implement_disposable()
    {
        DisposableElement.DisposedCount = 0;
        Rlp.ValueDecoderContext ctx = new(new byte[] { 0xc2, 0x01, 0x02 });

        RlpException? exception = null;
        try
        {
            Rlp.DecodeArrayPool<object>(ref ctx, new ThrowingObjectDecoder());
        }
        catch (RlpException e)
        {
            exception = e;
        }

        Assert.That(exception?.Message, Is.EqualTo(ThrowingObjectDecoder.Error));
        Assert.That(DisposableElement.DisposedCount, Is.EqualTo(1));
    }

    [Test]
    public void DecodeArrayPool_wraps_non_rlp_decoder_exceptions()
    {
        Rlp.ValueDecoderContext ctx = new(new byte[] { 0xc1, 0x01 });

        RlpException? exception = null;
        try
        {
            Rlp.DecodeArrayPool(ref ctx, new ThrowingArgumentDecoder());
        }
        catch (RlpException e)
        {
            exception = e;
        }

        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.InnerException, Is.TypeOf<ArgumentException>());
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
    public void Decode_slot_changes_rejects_storage_change_count_above_max_txs()
    {
        byte[] encoded = EncodeSlotChangesWithEmptyStorageChangeEntries(Eip7928Constants.MaxTxs + 1);

        Assert.That(
            () => Rlp.Decode<SlotChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpLimitException>()
                .With.Message.Contains($"over limit {Eip7928Constants.MaxTxs}"));
    }

    [Test]
    public void Decoded_slot_changes_uses_prestate_aware_comparer()
    {
        // Wire path: AccountChangesDecoder/SlotChangesDecoder build SortedLists with
        // PrestateAwareIndexComparer so internal generated-BAL journaling can graft a
        // PrestateIndex entry afterwards and keep it sorted first.
        StorageChange change = new(0, 0xCC);
        SlotChanges seed = new(7u, new SortedList<uint, StorageChange>(PrestateAwareIndexComparer.Instance) { { 0, change } });
        byte[] rlp = Rlp.Encode(seed).Bytes;

        Rlp.ValueDecoderContext ctx = new(rlp);
        SlotChanges decoded = SlotChangesDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);

        // Graft a prestate entry as generated-BAL journaling does, then verify it lands first.
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
    public void Balance_change_with_prestate_index_is_rejected()
    {
        byte[] rlp = [0xc6, 0x84, 0xff, 0xff, 0xff, 0xff, 0x01];

        Rlp.ValueDecoderContext ctx = new(rlp);
        RlpException? exception = null;
        try
        {
            BalanceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        }
        catch (RlpException e)
        {
            exception = e;
        }

        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("reserved for internal prestate"));
        Assert.That(
            () => Rlp.Encode(new BalanceChange(Eip7928Constants.PrestateIndex, 0x1)),
            Throws.TypeOf<RlpException>().With.Message.Contain("reserved for internal prestate"));
    }

    [Test]
    public void Block_access_list_with_wire_prestate_index_is_rejected()
    {
        byte[] rlp =
        [
            0xe2, 0xe1, 0x94,
            0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00,
            0xc0, 0xc0,
            0xc7, 0xc6, 0x84, 0xff, 0xff, 0xff, 0xff, 0x01,
            0xc0, 0xc0
        ];

        Assert.That(
            () => Rlp.Decode<BlockAccessList>(rlp),
            Throws.TypeOf<RlpException>().With.Message.Contain("reserved for internal prestate"));
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
        SortByAddress(ref accountChangesA, ref accountChangesB);

        byte[] encoded = EncodeAccountChangesSequence(accountChangesB, accountChangesA);

        Assert.That(
            () => Rlp.Decode<BlockAccessList>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Account changes were in incorrect order."));
    }

    [Test]
    public void Encoding_block_access_list_orders_account_changes_by_address()
    {
        AccountChanges accountChangesA = Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject;
        AccountChanges accountChangesB = Build.An.AccountChanges.WithAddress(TestItem.AddressB).TestObject;
        SortByAddress(ref accountChangesA, ref accountChangesB);
        BlockAccessList blockAccessList = new();
        blockAccessList.AddAccountChanges(accountChangesB, accountChangesA);

        byte[] encoded = Rlp.Encode(blockAccessList, RlpBehaviors.None).Bytes;
        byte[] expected = EncodeAccountChangesSequence(accountChangesA, accountChangesB);

        Assert.That(encoded, Is.EqualTo(expected));
    }

    [Test]
    public void GetLength_block_access_list_does_not_sort_account_changes()
    {
        AccountChanges accountChangesA = Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject;
        AccountChanges accountChangesB = Build.An.AccountChanges.WithAddress(TestItem.AddressB).TestObject;
        SortByAddress(ref accountChangesA, ref accountChangesB);
        BlockAccessList blockAccessList = new();
        blockAccessList.AddAccountChanges(accountChangesB, accountChangesA);

        FieldInfo sortedAccountChangesField = PrivateField<BlockAccessList>("_sortedAccountChanges");

        BlockAccessListDecoder.Instance.GetLength(blockAccessList, RlpBehaviors.None);

        Assert.That(sortedAccountChangesField.GetValue(blockAccessList), Is.Null);

        Rlp.Encode(blockAccessList, RlpBehaviors.None);

        Assert.That(sortedAccountChangesField.GetValue(blockAccessList), Is.Not.Null);
    }

    [Test]
    public void GetLength_account_changes_does_not_sort_storage_changes()
    {
        AccountChanges accountChanges = new(TestItem.AddressA);
        accountChanges.GetOrAddSlotChanges(9u).AddStorageChange(new StorageChange(0u, 0x99));
        accountChanges.GetOrAddSlotChanges(1u).AddStorageChange(new StorageChange(0u, 0x11));

        FieldInfo sortedStorageChangesField = PrivateField<AccountChanges>("_sortedStorageChanges");

        AccountChangesDecoder.Instance.GetLength(accountChanges, RlpBehaviors.None);

        Assert.That(sortedStorageChangesField.GetValue(accountChanges), Is.Null);

        Rlp.Encode(accountChanges, RlpBehaviors.None);

        Assert.That(sortedStorageChangesField.GetValue(accountChanges), Is.Not.Null);
    }

    [Test]
    public void EncodeToBytes_matches_stream_encoder()
    {
        StorageChange storageChange = new(1, 0x11);
        AccountChanges accountChangesA = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageChanges(0x22, storageChange)
            .WithStorageReads(0x33, 0x44)
            .WithBalanceChanges([new(1, 0x55)])
            .WithNonceChanges([new(1, 0x66)])
            .WithCodeChanges(new CodeChange(1, [0x77]))
            .TestObject;
        AccountChanges accountChangesB = Build.An.AccountChanges.WithAddress(TestItem.AddressB).TestObject;
        SortByAddress(ref accountChangesA, ref accountChangesB);
        BlockAccessList blockAccessList = new();
        blockAccessList.AddAccountChanges(accountChangesB, accountChangesA);

        int length = BlockAccessListDecoder.Instance.GetLength(blockAccessList, RlpBehaviors.None);
        RlpStream stream = new(length);
        BlockAccessListDecoder.Instance.Encode(stream, blockAccessList, RlpBehaviors.None);

        byte[] direct = BlockAccessListDecoder.Instance.EncodeToBytes(blockAccessList, RlpBehaviors.None);

        Assert.That(direct, Is.EqualTo(stream.Data.ToArray()));
        Assert.That(Rlp.Encode(blockAccessList, RlpBehaviors.None).Bytes, Is.EqualTo(direct));
    }

    [Test]
    public void Encoding_account_changes_orders_unsorted_generated_storage_changes()
    {
        AccountChanges accountChanges = new(TestItem.AddressA);
        accountChanges.GetOrAddSlotChanges(9u).AddStorageChange(new StorageChange(0u, 0x99));
        accountChanges.GetOrAddSlotChanges(1u).AddStorageChange(new StorageChange(0u, 0x11));

        byte[] encoded = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;
        AccountChanges decoded = Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None);

        Assert.That(decoded.StorageChanges[0].Key, Is.EqualTo((UInt256)1u));
        Assert.That(decoded.StorageChanges[1].Key, Is.EqualTo((UInt256)9u));
        Assert.That(decoded, Is.EqualTo(accountChanges));
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
            storageChanges.Values.ToArray(),
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
        // Encode reads in descending order on the wire to exercise the decoder's
        // ascending-order check. The production encoder always emits sorted reads,
        // so we go directly through the test helper that takes the raw array.
        byte[] encoded = EncodeAccountChanges(
            TestItem.AddressA,
            [],
            [new UInt256(2), UInt256.One],
            [],
            [],
            []);

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Storage reads were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_balance_changes_throws()
    {
        byte[] encoded = EncodeAccountChanges(
            TestItem.AddressA,
            [],
            [],
            [new(2, UInt256.Zero), new(1, UInt256.One)],
            [],
            []);

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Balance changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_duplicate_balance_change_indices_throws()
    {
        byte[] encoded = EncodeAccountChanges(
            TestItem.AddressA,
            [],
            [],
            [new(1, UInt256.One), new(1, UInt256.Zero)],
            [],
            []);

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Balance changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_nonce_changes_throws()
    {
        byte[] encoded = EncodeAccountChanges(
            TestItem.AddressA,
            [],
            [],
            [],
            [new(2, 2), new(1, 1)],
            []);

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Nonce changes were in incorrect order."));
    }

    [Test]
    public void Decoding_account_changes_with_unsorted_code_changes_throws()
    {
        byte[] encoded = EncodeAccountChanges(
            TestItem.AddressA,
            [],
            [],
            [],
            [],
            [new(2, [0x02]), new(1, [0x01])]);

        Assert.That(
            () => Rlp.Decode<AccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>().With.Message.EqualTo("Code changes were in incorrect order."));
    }

    [Test]
    public void Decoding_slot_changes_with_unsorted_storage_changes_throws()
    {
        byte[] encoded = EncodeSlotChanges(UInt256.One, [new(2, UInt256.Zero), new(1, UInt256.One)]);

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

    private static void SortByAddress(ref AccountChanges left, ref AccountChanges right)
    {
        if (left.Address.CompareTo(right.Address) > 0)
        {
            (left, right) = (right, left);
        }
    }

    private static byte[] EncodeAccountChangesSequence(params AccountChanges[] accountChanges)
    {
        Rlp[] encodedAccountChanges = new Rlp[accountChanges.Length];
        int contentLength = 0;
        for (int i = 0; i < accountChanges.Length; i++)
        {
            encodedAccountChanges[i] = Rlp.Encode(accountChanges[i], RlpBehaviors.None);
            contentLength += encodedAccountChanges[i].Length;
        }

        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        stream.StartSequence(contentLength);
        for (int i = 0; i < encodedAccountChanges.Length; i++)
        {
            stream.Encode(encodedAccountChanges[i]);
        }

        return stream.Data.ToArray()!;
    }

    private static byte[] EncodeAccountChanges(
        Address address,
        SlotChanges[] storageChanges,
        UInt256[] storageReads,
        BalanceChange[] balanceChanges,
        NonceChange[] nonceChanges,
        CodeChange[] codeChanges)
    {
        int contentLength = Rlp.LengthOfAddressRlp
            + GetArrayLength(storageChanges, SlotChangesDecoder.Instance)
            + GetArrayLength(storageReads, UInt256Decoder.Instance)
            + GetArrayLength(balanceChanges, BalanceChangeDecoder.Instance)
            + GetArrayLength(nonceChanges, NonceChangeDecoder.Instance)
            + GetArrayLength(codeChanges, CodeChangeDecoder.Instance);

        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        stream.StartSequence(contentLength);
        stream.Encode(address);
        EncodeArray(stream, storageChanges, SlotChangesDecoder.Instance);
        EncodeArray(stream, storageReads, UInt256Decoder.Instance);
        EncodeArray(stream, balanceChanges, BalanceChangeDecoder.Instance);
        EncodeArray(stream, nonceChanges, NonceChangeDecoder.Instance);
        EncodeArray(stream, codeChanges, CodeChangeDecoder.Instance);
        return stream.Data.ToArray()!;
    }

    private static byte[] EncodeAccountChangesWithEmptySlotChangesEntry(Address address)
    {
        int storageChangesLength = Rlp.LengthOfSequence(Rlp.OfEmptyList.Length);
        int contentLength = Rlp.LengthOfAddressRlp
            + storageChangesLength
            + Rlp.OfEmptyList.Length
            + Rlp.OfEmptyList.Length
            + Rlp.OfEmptyList.Length
            + Rlp.OfEmptyList.Length;

        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        stream.StartSequence(contentLength);
        stream.Encode(address);
        stream.StartSequence(Rlp.OfEmptyList.Length);
        stream.Encode(Rlp.OfEmptyList);
        stream.Encode(Rlp.OfEmptyList);
        stream.Encode(Rlp.OfEmptyList);
        stream.Encode(Rlp.OfEmptyList);
        stream.Encode(Rlp.OfEmptyList);
        return stream.Data.ToArray()!;
    }

    private static byte[] EncodeSlotChanges(UInt256 slot, StorageChange[] changes)
    {
        int contentLength = Rlp.LengthOf(slot) + GetArrayLength(changes, StorageChangeDecoder.Instance);
        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        stream.StartSequence(contentLength);
        stream.Encode(in slot);
        EncodeArray(stream, changes, StorageChangeDecoder.Instance);
        return stream.Data.ToArray()!;
    }

    private static byte[] EncodeSlotChangesWithEmptyStorageChangeEntries(int count)
    {
        int changesContentLength = count * Rlp.OfEmptyList.Length;
        int contentLength = Rlp.LengthOf(UInt256.Zero) + Rlp.LengthOfSequence(changesContentLength);
        RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
        stream.StartSequence(contentLength);
        stream.Encode(UInt256.Zero);
        stream.StartSequence(changesContentLength);
        for (int i = 0; i < count; i++)
        {
            stream.Encode(Rlp.OfEmptyList);
        }

        return stream.Data.ToArray()!;
    }

    private static int GetArrayLength<T>(T[] items, IRlpStreamEncoder<T> encoder)
    {
        int contentLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            contentLength += encoder.GetLength(items[i], RlpBehaviors.None);
        }

        return Rlp.LengthOfSequence(contentLength);
    }

    private static void EncodeArray<T>(RlpStream stream, T[] items, IRlpStreamEncoder<T> encoder)
    {
        int contentLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            contentLength += encoder.GetLength(items[i], RlpBehaviors.None);
        }

        stream.StartSequence(contentLength);
        for (int i = 0; i < items.Length; i++)
        {
            encoder.Encode(stream, items[i], RlpBehaviors.None);
        }
    }

    private static FieldInfo PrivateField<T>(string name) =>
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(T).FullName, name);

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

    private sealed class DisposableElement : IDisposable
    {
        public static int DisposedCount;

        public void Dispose() => DisposedCount++;
    }

    private sealed class ThrowingDisposableDecoder : IRlpValueDecoder<DisposableElement>
    {
        public const string Error = "disposable semantic failure";
        private int _calls;

        public DisposableElement Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            decoderContext.DecodeByte();
            _calls++;
            if (_calls == 2)
            {
                throw new RlpException(Error);
            }

            return new DisposableElement();
        }

        public int GetLength(DisposableElement item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => 1;
    }

    private sealed class ThrowingObjectDecoder : IRlpValueDecoder<object>
    {
        public const string Error = "object semantic failure";
        private int _calls;

        public object Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            decoderContext.DecodeByte();
            _calls++;
            if (_calls == 2)
            {
                throw new RlpException(Error);
            }

            return new DisposableElement();
        }

        public int GetLength(object item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => 1;
    }

    private sealed class ThrowingArgumentDecoder : IRlpValueDecoder<byte>
    {
        public byte Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            decoderContext.DecodeByte();
            throw new ArgumentException("semantic argument failure");
        }

        public int GetLength(byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => 1;
    }
}
