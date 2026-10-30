// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.Eip7928;

public sealed class AccountChangesDecoder : RlpDecoder<ReadOnlyAccountChanges>
{
    public static readonly AccountChangesDecoder Instance = new();

    /// <summary>Cached sub-sequence content lengths for one account, computed by
    /// <see cref="PrepareEncodingLengths(ReadOnlyAccountChanges, RlpBehaviors)"/> /
    /// <see cref="PrepareEncodingLengths(GeneratedAccountChanges, RlpBehaviors)"/> and
    /// consumed by <c>EncodePrepared</c> so the encode pass never re-walks the per-account
    /// collections to recompute the same totals.</summary>
    internal readonly record struct EncodingLengths(
        int ContentLength,
        int StorageChangesContentLength,
        int StorageReadsContentLength,
        int BalanceContentLength,
        int NonceContentLength,
        int CodeContentLength);

    private static readonly RlpLimit _slotsLimit = new(Eip7928Constants.MaxSlots, "", ReadOnlyMemory<char>.Empty);
    private static readonly RlpLimit _storageLimit = new(Eip7928Constants.MaxSlots, "", ReadOnlyMemory<char>.Empty);

    protected override ReadOnlyAccountChanges DecodeInternal(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0 ? Decode<OffFlag>(ref reader, rlpBehaviors) : Decode<OnFlag>(ref reader, rlpBehaviors);

    internal static ReadOnlyAccountChanges Decode<TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TCanonicalChecks : struct, IFlag
    {
        RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int length);

        Address address = fields.DecodeAddress();

        ReadOnlySlotChanges[] slotChanges = DecodeSlotChanges<TCanonicalChecks>(ref fields);
        UInt256? lastSlot = null;
        foreach (ReadOnlySlotChanges slotChange in slotChanges)
        {
            UInt256 slot = slotChange.Key;
            if (lastSlot is not null && slot <= lastSlot)
            {
                ThrowStorageChangesOutOfOrder();
            }
            lastSlot = slot;
        }

        UInt256[] storageReads = DecodeStorageReads<TCanonicalChecks>(ref fields);
        UInt256? lastRead = null;
        foreach (UInt256 storageRead in storageReads)
        {
            if (lastRead is not null && storageRead.CompareTo(lastRead.Value) <= 0)
            {
                ThrowStorageReadsOutOfOrder();
            }
            // slotChanges is sorted ascending (verified above), so a binary search avoids the
            // per-account HashSet allocation a naive O(1) approach would require.
            if (ContainsStorageChange(slotChanges, storageRead))
            {
                ThrowInvalidStorageRead();
            }
            lastRead = storageRead;
        }

        RlpLimit changesLimit = SlotChangesDecoder.ChangesLimit;
        BalanceChange[] balanceChanges = IndexedChangeDecoder<BalanceChange>.DecodeArray<BalanceChangeDecoder, TCanonicalChecks>(ref fields, changesLimit);
        ValidateSortedByIndex(balanceChanges, "Balance");

        NonceChange[] nonceChanges = IndexedChangeDecoder<NonceChange>.DecodeArray<NonceChangeDecoder, TCanonicalChecks>(ref fields, changesLimit);
        ValidateSortedByIndex(nonceChanges, "Nonce");

        CodeChange[] codeChanges = IndexedChangeDecoder<CodeChange>.DecodeArray<CodeChangeDecoder, TCanonicalChecks>(ref fields, changesLimit);
        ValidateSortedByIndex(codeChanges, "Code");

        reader.ExitSequence(in fields, length, rlpBehaviors);

        return new ReadOnlyAccountChanges(address, slotChanges, storageReads, balanceChanges, nonceChanges, codeChanges);
    }

    private static ReadOnlySlotChanges[] DecodeSlotChanges<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemsLength);
        int count = items.CountItems<TCanonicalChecks>(_slotsLimit);
        ReadOnlySlotChanges[] result = count == 0 ? [] : new ReadOnlySlotChanges[count];
        // A span store has no covariance check, unlike an array store of a reference type.
        Span<ReadOnlySlotChanges> span = result;
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = SlotChangesDecoder.Decode<TCanonicalChecks>(ref items, RlpBehaviors.None);
        }

        reader.ExitSequence(in items, itemsLength);
        return result;
    }

    private static UInt256[] DecodeStorageReads<TCanonicalChecks>(ref RlpReader reader) where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemsLength);
        int count = items.CountItems<TCanonicalChecks>(_storageLimit);
        UInt256[] result = count == 0 ? [] : new UInt256[count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = items.DecodeUInt256<TCanonicalChecks>();
        }

        reader.ExitSequence(in items, itemsLength);
        return result;
    }

    public override int GetLength(ReadOnlyAccountChanges? item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item ?? throw new ArgumentNullException(nameof(item)), rlpBehaviors));

    public int GetLength(GeneratedAccountChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public override void Encode(ref RlpWriter writer, ReadOnlyAccountChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        EncodingLengths lengths = PrepareEncodingLengths(item, rlpBehaviors);
        EncodePrepared(ref writer, item, in lengths, rlpBehaviors);
    }

    public void Encode(ref RlpWriter writer, GeneratedAccountChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        EncodingLengths lengths = PrepareEncodingLengths(item, rlpBehaviors);
        EncodePrepared(ref writer, item, in lengths, rlpBehaviors);
    }

    internal void EncodePrepared(
        ref RlpWriter writer,
        ReadOnlyAccountChanges item,
        in EncodingLengths lengths,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpSequence sequence = writer.StartSequence(lengths.ContentLength);
        writer.Encode(item.Address);
        EncodeSlotChanges(ref writer, item.StorageChanges, lengths.StorageChangesContentLength, rlpBehaviors);
        EncodeStorageReads(ref writer, item.StorageReads, lengths.StorageReadsContentLength, rlpBehaviors);
        EncodeIndexed<BalanceChange>(ref writer, item.BalanceChanges, lengths.BalanceContentLength, BalanceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexed<NonceChange>(ref writer, item.NonceChanges, lengths.NonceContentLength, NonceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexed<CodeChange>(ref writer, item.CodeChanges, lengths.CodeContentLength, CodeChangeDecoder.Instance, rlpBehaviors);
        writer.EndSequence(sequence);
    }

    internal void EncodePrepared(
        ref RlpWriter writer,
        GeneratedAccountChanges item,
        in EncodingLengths lengths,
        RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        RlpSequence sequence = writer.StartSequence(lengths.ContentLength);
        writer.Encode(item.Address);
        using (ArrayPoolListRef<GeneratedSlotChanges> sortedSlots = item.GetSortedStorageChanges())
        {
            EncodeGeneratedSlotChanges(ref writer, sortedSlots.AsSpan(), lengths.StorageChangesContentLength, rlpBehaviors);
        }
        using (ArrayPoolListRef<UInt256> sortedReads = item.GetSortedStorageReads())
        {
            EncodeStorageReads(ref writer, sortedReads.AsSpan(), lengths.StorageReadsContentLength, rlpBehaviors);
        }
        EncodeIndexed<BalanceChange>(ref writer, CollectionsMarshal.AsSpan(item.BalanceChanges), lengths.BalanceContentLength, BalanceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexed<NonceChange>(ref writer, CollectionsMarshal.AsSpan(item.NonceChanges), lengths.NonceContentLength, NonceChangeDecoder.Instance, rlpBehaviors);
        EncodeIndexed<CodeChange>(ref writer, CollectionsMarshal.AsSpan(item.CodeChanges), lengths.CodeContentLength, CodeChangeDecoder.Instance, rlpBehaviors);
        writer.EndSequence(sequence);
    }

    public static int GetContentLength(ReadOnlyAccountChanges item, RlpBehaviors rlpBehaviors)
        => PrepareEncodingLengths(item, rlpBehaviors).ContentLength;

    public static int GetContentLength(GeneratedAccountChanges item, RlpBehaviors rlpBehaviors)
        => PrepareEncodingLengths(item, rlpBehaviors).ContentLength;

    internal static EncodingLengths PrepareEncodingLengths(ReadOnlyAccountChanges item, RlpBehaviors rlpBehaviors)
    {
        int storageChanges = SlotChangesContentLength(item.StorageChanges, rlpBehaviors);
        int storageReads = UInt256ContentLength(item.StorageReads, rlpBehaviors);
        int balance = IndexedContentLength<BalanceChange>(item.BalanceChanges, BalanceChangeDecoder.Instance, rlpBehaviors);
        int nonce = IndexedContentLength<NonceChange>(item.NonceChanges, NonceChangeDecoder.Instance, rlpBehaviors);
        int code = IndexedContentLength<CodeChange>(item.CodeChanges, CodeChangeDecoder.Instance, rlpBehaviors);
        return BuildLengths(storageChanges, storageReads, balance, nonce, code);
    }

    internal static EncodingLengths PrepareEncodingLengths(GeneratedAccountChanges item, RlpBehaviors rlpBehaviors)
    {
        int storageChanges = GeneratedSlotChangesContentLength(item.StorageChanges, rlpBehaviors);
        int storageReads = UInt256ContentLength(item.StorageReads, rlpBehaviors);
        int balance = IndexedContentLength<BalanceChange>(CollectionsMarshal.AsSpan(item.BalanceChanges), BalanceChangeDecoder.Instance, rlpBehaviors);
        int nonce = IndexedContentLength<NonceChange>(CollectionsMarshal.AsSpan(item.NonceChanges), NonceChangeDecoder.Instance, rlpBehaviors);
        int code = IndexedContentLength<CodeChange>(CollectionsMarshal.AsSpan(item.CodeChanges), CodeChangeDecoder.Instance, rlpBehaviors);
        return BuildLengths(storageChanges, storageReads, balance, nonce, code);
    }

    private static EncodingLengths BuildLengths(int storageChanges, int storageReads, int balance, int nonce, int code)
    {
        int contentLength = Rlp.LengthOfAddressRlp
            + Rlp.LengthOfSequence(storageChanges)
            + Rlp.LengthOfSequence(storageReads)
            + Rlp.LengthOfSequence(balance)
            + Rlp.LengthOfSequence(nonce)
            + Rlp.LengthOfSequence(code);
        return new EncodingLengths(contentLength, storageChanges, storageReads, balance, nonce, code);
    }

    private static int SlotChangesContentLength(scoped ReadOnlySpan<ReadOnlySlotChanges> items, RlpBehaviors rlpBehaviors)
    {
        SlotChangesDecoder decoder = SlotChangesDecoder.Instance;
        int len = 0;
        for (int i = 0; i < items.Length; i++)
        {
            len += decoder.GetLength(items[i], rlpBehaviors);
        }
        return len;
    }

    private static int GeneratedSlotChangesContentLength(IReadOnlyCollection<GeneratedSlotChanges> items, RlpBehaviors rlpBehaviors)
    {
        SlotChangesDecoder decoder = SlotChangesDecoder.Instance;
        int len = 0;
        foreach (GeneratedSlotChanges s in items) len += decoder.GetLength(s, rlpBehaviors);
        return len;
    }

    private static int UInt256ContentLength(scoped ReadOnlySpan<UInt256> items, RlpBehaviors rlpBehaviors)
    {
        UInt256Decoder decoder = UInt256Decoder.Instance;
        int len = 0;
        for (int i = 0; i < items.Length; i++)
        {
            len += decoder.GetLength(items[i], rlpBehaviors);
        }
        return len;
    }

    private static int UInt256ContentLength(IReadOnlyCollection<UInt256> items, RlpBehaviors rlpBehaviors)
    {
        UInt256Decoder decoder = UInt256Decoder.Instance;
        int len = 0;
        foreach (UInt256 v in items) len += decoder.GetLength(v, rlpBehaviors);
        return len;
    }

    private static int IndexedContentLength<T>(scoped ReadOnlySpan<T> items, RlpDecoder<T> encoder, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        for (int i = 0; i < items.Length; i++)
        {
            len += encoder.GetLength(items[i], rlpBehaviors);
        }
        return len;
    }

    private static void EncodeSlotChanges(ref RlpWriter writer, scoped ReadOnlySpan<ReadOnlySlotChanges> items, int contentLength, RlpBehaviors rlpBehaviors)
    {
        SlotChangesDecoder decoder = SlotChangesDecoder.Instance;
        RlpSequence sequence = writer.StartSequence(contentLength);
        for (int i = 0; i < items.Length; i++)
        {
            decoder.Encode(ref writer, items[i], rlpBehaviors);
        }

        writer.EndSequence(sequence);
    }

    private static void EncodeGeneratedSlotChanges(ref RlpWriter writer, scoped ReadOnlySpan<GeneratedSlotChanges> items, int contentLength, RlpBehaviors rlpBehaviors)
    {
        SlotChangesDecoder decoder = SlotChangesDecoder.Instance;
        RlpSequence sequence = writer.StartSequence(contentLength);
        for (int i = 0; i < items.Length; i++)
        {
            decoder.Encode(ref writer, items[i], rlpBehaviors);
        }

        writer.EndSequence(sequence);
    }

    private static void EncodeStorageReads(ref RlpWriter writer, scoped ReadOnlySpan<UInt256> items, int contentLength, RlpBehaviors rlpBehaviors)
    {
        RlpSequence sequence = writer.StartSequence(contentLength);
        for (int i = 0; i < items.Length; i++)
        {
            writer.Encode(items[i]);
        }

        writer.EndSequence(sequence);
    }

    private static void EncodeIndexed<T>(ref RlpWriter writer, scoped ReadOnlySpan<T> items, int contentLength, IndexedChangeDecoder<T> encoder, RlpBehaviors rlpBehaviors)
        where T : struct, IIndexedChange
    {
        RlpSequence sequence = writer.StartSequence(contentLength);
        for (int i = 0; i < items.Length; i++)
        {
            encoder.Encode(ref writer, items[i], rlpBehaviors);
        }

        writer.EndSequence(sequence);
    }

    private static void ValidateSortedByIndex<T>(T[] items, string changeName)
        where T : struct, IIndexedChange
    {
        uint? lastIndex = null;
        foreach (T item in items)
        {
            uint index = item.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                ThrowIndexedChangesOutOfOrder(changeName);
            }
            lastIndex = index;
        }
    }

    /// <summary>Binary search over <paramref name="sortedSlotChanges"/> (sorted by key, verified
    /// during decode) for <paramref name="key"/>. O(log n) lookup with no allocation, matching
    /// master's behaviour for the storage-read-vs-change overlap check.</summary>
    private static bool ContainsStorageChange(ReadOnlySlotChanges[] sortedSlotChanges, in UInt256 key)
    {
        int low = 0;
        int high = sortedSlotChanges.Length - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int compare = sortedSlotChanges[mid].Key.CompareTo(key);
            if (compare == 0)
            {
                return true;
            }

            if (compare < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return false;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageChangesOutOfOrder() =>
        throw new RlpReaderException("Storage changes were in incorrect order.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageReadsOutOfOrder() =>
        throw new RlpReaderException("Storage reads were in incorrect order.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidStorageRead() =>
        throw new RlpReaderException("Invalid storage read, already in storage changes.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowIndexedChangesOutOfOrder(string changeName) =>
        throw new RlpReaderException($"{changeName} changes were in incorrect order.");
}
