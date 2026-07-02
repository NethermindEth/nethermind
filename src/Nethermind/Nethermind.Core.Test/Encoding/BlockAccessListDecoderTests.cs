// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
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
        ReadOnlyBlockAccessList bal = Rlp.Decode<ReadOnlyBlockAccessList>(Bytes.FromHexString(rlp))!;

        Assert.That(bal, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(bal).Bytes);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [TestCaseSource(nameof(BlockAccessListTestSource))]
    public void Decode_caches_wire_hash_matching_full_rlp_keccak(string rlp, ReadOnlyBlockAccessList _)
    {
        byte[] bytes = Bytes.FromHexString(rlp);
        ReadOnlyBlockAccessList bal = Rlp.Decode<ReadOnlyBlockAccessList>(bytes)!;

        Assert.That(bal.WireHash, Is.Not.Null);
        Assert.That(bal.WireHash, Is.EqualTo(new Hash256(ValueKeccak.Compute(bytes))));
    }

    [Test]
    public void Decode_handles_envelope_with_trailing_bytes_and_hashes_only_the_bal_slice()
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject)
            .TestObject;
        byte[] balRlp = Rlp.Encode(bal).Bytes;

        byte[] envelope = new byte[balRlp.Length + 4];
        Buffer.BlockCopy(balRlp, 0, envelope, 0, balRlp.Length);
        envelope[^4] = 0xde;
        envelope[^3] = 0xad;
        envelope[^2] = 0xbe;
        envelope[^1] = 0xef;

        RlpReader ctx = new(envelope);
        ReadOnlyBlockAccessList decoded = BlockAccessListDecoder.Instance.DecodeGuardNotNull(ref ctx, RlpBehaviors.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.WireHash, Is.EqualTo(new Hash256(ValueKeccak.Compute(balRlp))));
            Assert.That(ctx.Position, Is.EqualTo(balRlp.Length));
        }
    }

    // Truncated RLP causes an out-of-bounds primitive read; the Rlp.Decode entry-point
    // wrap converts that to RlpException so callers see a consistent failure mode
    // (engine_newPayloadV5 returns a clean error instead of crashing the RPC).
    [Test]
    public void Decode_empty_bytes_throws_RlpException() =>
        Assert.That(() => Rlp.Decode<ReadOnlyBlockAccessList>([]), Throws.TypeOf<RlpException>());

    // 0xf8 announces a long-form list with a 1-byte length follower, but the byte is missing.
    [Test]
    public void Decode_truncated_outer_list_throws_RlpException() =>
        Assert.That(() => Rlp.Decode<ReadOnlyBlockAccessList>(new byte[] { 0xf8 }), Throws.TypeOf<RlpException>());

    // 0x80 is an empty byte string, not an EIP-7928 BAL list. The public Decode<T>
    // entry point must fail with a typed RlpException rather than returning null.
    [Test]
    public void Decode_empty_string_throws_RlpException() =>
        Assert.That(() => Rlp.Decode<ReadOnlyBlockAccessList>(new byte[] { 0x80 }), Throws.TypeOf<RlpException>());

    // 0xc1 0xc0 = outer list of 1 containing an empty inner list. EIP-7928 requires each
    // AccountChanges to be a 6-field sequence; an empty list is structurally invalid.
    [Test]
    public void Decode_inner_empty_list_in_account_changes_throws_RlpException() =>
        Assert.That(() => Rlp.Decode<ReadOnlyBlockAccessList>(new byte[] { 0xc1, 0xc0 }), Throws.TypeOf<RlpException>());

    [Test]
    public void Decode_empty_slot_changes_entry_in_account_changes_throws_RlpException()
    {
        byte[] encoded = EncodeAccountChangesWithEmptySlotChangesEntry(TestItem.AddressA);

        Assert.That(
            () => Rlp.Decode<ReadOnlyAccountChanges>(encoded),
            Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Decode_account_changes_without_changes_or_reads_roundtrips_as_account_read()
    {
        ReadOnlyBlockAccessList bal = new(
            [new ReadOnlyAccountChanges(TestItem.AddressA)],
            itemCount: 0);
        byte[] encoded = Rlp.Encode(bal).Bytes;

        ReadOnlyBlockAccessList decoded = Rlp.Decode<ReadOnlyBlockAccessList>(encoded)!;

        Assert.That(decoded, Is.EqualTo(bal));
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
            RlpReader ctx = new(new byte[] { 0xc2, 0x01, 0x02 });

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
        RlpReader ctx = new(new byte[] { 0xc2, 0x01, 0x02 });

        RlpException? exception = null;
        try
        {
            Rlp.DecodeArrayPool(ref ctx, new ThrowingDisposableDecoder());
        }
        catch (RlpException e)
        {
            exception = e;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception?.Message, Is.EqualTo(ThrowingDisposableDecoder.Error));
            Assert.That(DisposableElement.DisposedCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void DecodeArrayPool_disposes_runtime_disposable_items_when_static_type_does_not_implement_disposable()
    {
        DisposableElement.DisposedCount = 0;
        RlpReader ctx = new(new byte[] { 0xc2, 0x01, 0x02 });

        RlpException? exception = null;
        try
        {
            Rlp.DecodeArrayPool<object>(ref ctx, new ThrowingObjectDecoder());
        }
        catch (RlpException e)
        {
            exception = e;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception?.Message, Is.EqualTo(ThrowingObjectDecoder.Error));
            Assert.That(DisposableElement.DisposedCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void DecodeArrayPool_wraps_non_rlp_decoder_exceptions() => Assert.That(
        () => { RlpReader c = new(new byte[] { 0xc1, 0x01 }); Rlp.DecodeArrayPool(ref c, new ThrowingArgumentDecoder()); },
        Throws.TypeOf<RlpException>().With.InnerException.TypeOf<ArgumentException>());

    [Test]
    public void Decode_slot_changes_with_empty_accesses_throws_RlpException()
    {
        // SlotChanges = [StorageKey, List[StorageChange]]. An empty StorageChange list means a
        // slot with no changes — that slot belongs in storage_reads instead. Geth bal-devnet-4
        // rejects this with "empty storage writes".
        ReadOnlySlotChanges withEmptyChanges = new(123u, []);
        byte[] rlp = Rlp.Encode(withEmptyChanges).Bytes;

        RlpException? thrown = null;
        try
        {
            RlpReader ctx = new(rlp);
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
            () => Rlp.Decode<ReadOnlySlotChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpLimitException>()
                .With.Message.Contains($"over limit {Eip7928Constants.MaxTxs}"));
    }

    [Test]
    public void DecodeArray_with_decoder_stays_constrained_to_reference_types()
    {
        MethodInfo decodeArray = typeof(RlpReader)
            .GetMethods()
            .Single(m => m.Name == nameof(RlpReader.DecodeArray)
                && m.IsGenericMethodDefinition
                && m.GetParameters()[0].ParameterType.IsGenericType
                && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IRlpDecoder<>));

        GenericParameterAttributes constraints = decodeArray.GetGenericArguments()[0].GenericParameterAttributes;

        Assert.That(constraints.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint), Is.True,
            "DecodeArray(IRlpDecoder<T>, ...) must stay constrained to reference types: a value-type T would otherwise silently substitute default(T) for an empty-list (0xc0) element instead of throwing.");
    }

    [Test]
    public void Decode_slot_changes_with_empty_list_storage_change_throws_RlpException()
    {
        byte[] encoded = EncodeSlotChangesWithEmptyStorageChangeEntries(1);

        Assert.That(
            () => Rlp.Decode<ReadOnlySlotChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>());
    }

    [TestCase(0, TestName = "storage_changes")]
    [TestCase(1, TestName = "storage_reads")]
    [TestCase(2, TestName = "balance_changes")]
    [TestCase(3, TestName = "nonce_changes")]
    [TestCase(4, TestName = "code_changes")]
    public void Decode_account_changes_with_empty_list_element_in_field_throws_RlpException(int malformedFieldIndex)
    {
        byte[] encoded = EncodeAccountChangesWithEmptyListElement(TestItem.AddressA, malformedFieldIndex);

        Assert.That(
            () => Rlp.Decode<ReadOnlyAccountChanges>(encoded, RlpBehaviors.None),
            Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Can_decode_then_encode_balance_change()
    {
        const string rlp = "0xc801861319718811c8";
        RlpReader ctx = new(Bytes.FromHexString(rlp));
        BalanceChange balanceChange = BalanceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        BalanceChange expected = new(1, 0x1319718811c8);
        Assert.That(balanceChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(balanceChange).Bytes);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Balance_change_roundtrips_with_index_above_uint16_range()
    {
        // EIP-7928 widened BlockAccessIndex to uint32 (commit 645099785a). This test catches
        // any regression to the old uint16 decoder by using an index above 65535.
        BalanceChange original = new(0x10_0000u, 0x42);

        Rlp encoded = Rlp.Encode(original);
        RlpReader ctx = new(encoded.Bytes);
        BalanceChange decoded = BalanceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);

        Assert.That(decoded, Is.EqualTo(original));
        Assert.That(decoded.Index, Is.EqualTo(0x10_0000u));
    }

    [Test]
    public void Can_decode_then_encode_nonce_change()
    {
        const string rlp = "0xc20101";
        RlpReader ctx = new(Bytes.FromHexString(rlp));
        NonceChange nonceChange = NonceChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        NonceChange expected = new(1, 1);
        Assert.That(nonceChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(nonceChange).Bytes);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_decode_then_encode_slot_change()
    {
        StorageChange parentHashStorageChange = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));
        ReadOnlySlotChanges expected = new(0, [parentHashStorageChange]);

        string expectedRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);

        RlpReader ctx = new(Bytes.FromHexString(expectedRlp));
        ReadOnlySlotChanges slotChange = SlotChangesDecoder.Instance.DecodeGuardNotNull(ref ctx, RlpBehaviors.None);
        Assert.That(slotChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(slotChange).Bytes);
        Assert.That(encoded, Is.EqualTo(expectedRlp));
    }

    [Test]
    public void Can_decode_then_encode_storage_change()
    {
        StorageChange expected = new(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true));

        string expectedRlp = "0x" + Bytes.ToHexString(Rlp.Encode(expected).Bytes);

        RlpReader ctx = new(Bytes.FromHexString(expectedRlp));
        StorageChange storageChange = StorageChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        Assert.That(storageChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(storageChange).Bytes);
        Assert.That(encoded, Is.EqualTo(expectedRlp));
    }

    [Test]
    public void Can_decode_then_encode_code_change()
    {
        const string rlp = "0xc20100";

        RlpReader ctx = new(Bytes.FromHexString(rlp));
        CodeChange codeChange = CodeChangeDecoder.Instance.Decode(ref ctx, RlpBehaviors.None);
        CodeChange expected = new(1, [0x0]);
        Assert.That(codeChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(codeChange).Bytes);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [TestCaseSource(nameof(AccountChangesTestSource))]
    public void Can_decode_then_encode_account_change(string rlp, ReadOnlyAccountChanges expected)
    {
        RlpReader ctx = new(Bytes.FromHexString(rlp));
        ReadOnlyAccountChanges accountChange = AccountChangesDecoder.Instance.DecodeGuardNotNull(ref ctx, RlpBehaviors.None);

        Assert.That(accountChange, Is.EqualTo(expected));

        string encoded = "0x" + Bytes.ToHexString(Rlp.Encode(accountChange).Bytes);
        Assert.That(encoded, Is.EqualTo(rlp));
    }

    [Test]
    public void Can_encode_then_decode()
    {
        StorageChange storageChange = new(10, (UInt256)0xcad);
        byte[] storageChangeBytes = Rlp.Encode(storageChange, RlpBehaviors.None).Bytes;
        StorageChange storageChangeDecoded = Rlp.Decode<StorageChange>(storageChangeBytes, RlpBehaviors.None)!;
        Assert.That(storageChange, Is.EqualTo(storageChangeDecoded));

        StorageChange[] storageChanges = [storageChange];
        ReadOnlySlotChanges slotChanges = new(0xbad, storageChanges);
        byte[] slotChangesBytes = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;
        ReadOnlySlotChanges slotChangesDecoded = Rlp.Decode<ReadOnlySlotChanges>(slotChangesBytes, RlpBehaviors.None)!;
        Assert.That(slotChanges, Is.EqualTo(slotChangesDecoded));

        UInt256 storageRead = 0xbababa;
        byte[] storageReadBytes = Rlp.Encode(storageRead).Bytes;
        UInt256 storageReadDecoded = Rlp.Decode<UInt256>(storageReadBytes, RlpBehaviors.None);
        Assert.That(storageRead, Is.EqualTo(storageReadDecoded));

        BalanceChange balanceChange = new(10, 0);
        BalanceChange balanceChange2 = new(11, 1);
        byte[] balanceChangeBytes = Rlp.Encode(balanceChange, RlpBehaviors.None).Bytes;
        BalanceChange balanceChangeDecoded = Rlp.Decode<BalanceChange>(balanceChangeBytes, RlpBehaviors.None)!;
        Assert.That(balanceChange, Is.EqualTo(balanceChangeDecoded));

        NonceChange nonceChange = new(10, 0);
        NonceChange nonceChange2 = new(11, 0);
        byte[] nonceChangeBytes = Rlp.Encode(nonceChange, RlpBehaviors.None).Bytes;
        NonceChange nonceChangeDecoded = Rlp.Decode<NonceChange>(nonceChangeBytes, RlpBehaviors.None)!;
        Assert.That(nonceChange, Is.EqualTo(nonceChangeDecoded));

        CodeChange codeChange = new(10, [0, 50]);
        byte[] codeChangeBytes = Rlp.Encode(codeChange, RlpBehaviors.None).Bytes;
        CodeChange codeChangeDecoded = Rlp.Decode<CodeChange>(codeChangeBytes, RlpBehaviors.None)!;
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
        ReadOnlyAccountChanges accountChangesDecoded = Rlp.Decode<ReadOnlyAccountChanges>(accountChangesBytes, RlpBehaviors.None)!;
        Assert.That(accountChanges, Is.EqualTo(accountChangesDecoded));

        ReadOnlyBlockAccessList blockAccessList = Build.A.BlockAccessList.WithAccountChanges(accountChanges).TestObject;
        byte[] blockAccessListBytes = Rlp.Encode(blockAccessList, RlpBehaviors.None).Bytes;
        ReadOnlyBlockAccessList blockAccessListDecoded = Rlp.Decode<ReadOnlyBlockAccessList>(blockAccessListBytes, RlpBehaviors.None)!;
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
        // Each SlotChanges must have at least one StorageChange (per EIP-7928 / geth bal-devnet-4
        // "empty storage_changes" rejection), so add a real change to each.
        ReadOnlySlotChanges[] storageChanges =
        [
            new ReadOnlySlotChanges(slot2, [new StorageChange(0, 2)]),
            new ReadOnlySlotChanges(slot1, [new StorageChange(0, 1)])
        ];
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
    public void Decoding_account_changes_with_duplicate_balance_change_indices_throws()
    {
        BalanceChange[] balanceChanges = [new(1, UInt256.One), new(1, UInt256.Zero)];
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
                    [new StorageChange(0, new UInt256(Bytes.FromHexString("0xc382836f81d7e4055a0e280268371e17cc69a531efe2abee082e9b922d6050fd"), isBigEndian: true))])
                .TestObject;
            string storageChangesRlp = "0x" + Bytes.ToHexString(Rlp.Encode(storageChangesExpected).Bytes);
            yield return new TestCaseData(storageChangesRlp, storageChangesExpected) { TestName = "storage_changes" };
        }
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

        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        writer.Encode(address);
        writer.StartSequence(Rlp.OfEmptyList.Length);
        writer.Encode(Rlp.OfEmptyList);
        writer.Encode(Rlp.OfEmptyList);
        writer.Encode(Rlp.OfEmptyList);
        writer.Encode(Rlp.OfEmptyList);
        writer.Encode(Rlp.OfEmptyList);
        return bytes;
    }

    private static byte[] EncodeAccountChangesWithEmptyListElement(Address address, int malformedFieldIndex)
    {
        const int fieldCount = 5;
        int malformedFieldLength = Rlp.LengthOfSequence(Rlp.OfEmptyList.Length);

        int contentLength = Rlp.LengthOfAddressRlp;
        for (int i = 0; i < fieldCount; i++)
        {
            contentLength += i == malformedFieldIndex ? malformedFieldLength : Rlp.OfEmptyList.Length;
        }

        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        writer.Encode(address);
        for (int i = 0; i < fieldCount; i++)
        {
            if (i == malformedFieldIndex)
            {
                writer.StartSequence(Rlp.OfEmptyList.Length);
                writer.Encode(Rlp.OfEmptyList);
            }
            else
            {
                writer.Encode(Rlp.OfEmptyList);
            }
        }

        return bytes;
    }

    private static byte[] EncodeSlotChangesWithEmptyStorageChangeEntries(int count)
    {
        int changesContentLength = count * Rlp.OfEmptyList.Length;
        int contentLength = Rlp.LengthOf(UInt256.Zero) + Rlp.LengthOfSequence(changesContentLength);
        byte[] bytes = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(bytes);
        writer.StartSequence(contentLength);
        writer.Encode(UInt256.Zero);
        writer.StartSequence(changesContentLength);
        for (int i = 0; i < count; i++)
        {
            writer.Encode(Rlp.OfEmptyList);
        }

        return bytes;
    }

    private sealed class ThrowingByteDecoder : RlpDecoder<byte>
    {
        public const string Error = "semantic failure";
        private int _calls;

        protected override byte DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            byte value = decoderContext.DecodeByte();
            _calls++;
            if (_calls == 2)
            {
                throw new RlpException(Error);
            }

            return value;
        }

        public override int GetLength(byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => 1;

        public override void Encode<TWriter>(ref TWriter writer, byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            throw new NotSupportedException();
    }

    private sealed class DisposableElement : IDisposable
    {
        public static int DisposedCount;

        public void Dispose() => DisposedCount++;
    }

    private sealed class ThrowingDisposableDecoder : RlpDecoder<DisposableElement>
    {
        public const string Error = "disposable semantic failure";
        private int _calls;

        protected override DisposableElement DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            decoderContext.DecodeByte();
            _calls++;
            if (_calls == 2)
            {
                throw new RlpException(Error);
            }

            return new DisposableElement();
        }

        public override int GetLength(DisposableElement? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => 1;

        public override void Encode<TWriter>(ref TWriter writer, DisposableElement item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingObjectDecoder : RlpDecoder<object>
    {
        public const string Error = "object semantic failure";
        private int _calls;

        protected override object DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            decoderContext.DecodeByte();
            _calls++;
            if (_calls == 2)
            {
                throw new RlpException(Error);
            }

            return new DisposableElement();
        }

        public override int GetLength(object? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => 1;

        public override void Encode<TWriter>(ref TWriter writer, object item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingArgumentDecoder : RlpDecoder<byte>
    {
        protected override byte DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            decoderContext.DecodeByte();
            throw new ArgumentException("semantic argument failure");
        }

        public override int GetLength(byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => 1;

        public override void Encode<TWriter>(ref TWriter writer, byte item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            throw new NotSupportedException();
    }
}
