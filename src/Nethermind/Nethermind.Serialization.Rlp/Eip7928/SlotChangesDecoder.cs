// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp.Eip7928;

public class SlotChangesDecoder : RlpDecoder<ReadOnlySlotChanges>
{
    public static readonly SlotChangesDecoder Instance = new();

    /// <summary>Maximum number of changes to one account field or storage slot.</summary>
    /// <remarks>
    /// One change per block access index: the pre-execution index, one per transaction and the post-execution one.
    /// A property, as <see cref="RlpLimit.MaxBlockGas"/> that the transaction count follows is set from configuration.
    /// </remarks>
    internal static RlpLimit ChangesLimit => new(BlockBodyDecoder.TransactionsCountLimit.Limit + 2, "", ReadOnlyMemory<char>.Empty);

    protected override ReadOnlySlotChanges DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        UInt256 slot = ctx.DecodeUInt256();
        StorageChange[] changes = StorageChangeDecoder.Instance.DecodeNonNullArray(ref ctx, RlpBehaviors.None, ChangesLimit);

        // EIP-7928: a slot in storage_changes must have at least one change.
        // A slot with zero changes belongs in storage_reads instead.
        if (changes.Length == 0)
        {
            ThrowEmptyStorageChanges();
        }

        uint? lastIndex = null;
        foreach (StorageChange s in changes)
        {
            uint index = s.Index;
            if (lastIndex is not null && index <= lastIndex)
            {
                ThrowStorageChangesOutOfOrder(index, lastIndex.Value);
            }
            lastIndex = index;
        }
        ReadOnlySlotChanges slotChanges = new(slot, changes);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return slotChanges;
    }

    public override int GetLength(ReadOnlySlotChanges? item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item ?? throw new ArgumentNullException(nameof(item)), rlpBehaviors));

    public int GetLength(GeneratedSlotChanges item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public override void Encode<TWriter>(ref TWriter writer, ReadOnlySlotChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        => Encode(ref writer, item.Key, item.Changes, rlpBehaviors);

    public void Encode<TWriter>(ref TWriter writer, GeneratedSlotChanges item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
        => Encode(ref writer, item.Key, CollectionsMarshal.AsSpan(item.Changes), rlpBehaviors);

    public static int GetContentLength(ReadOnlySlotChanges item, RlpBehaviors rlpBehaviors)
        => GetContentLength(item.Key, StorageChangesContentLength(item.Changes, rlpBehaviors));

    public static int GetContentLength(GeneratedSlotChanges item, RlpBehaviors rlpBehaviors)
        => GetContentLength(item.Key, StorageChangesContentLength(CollectionsMarshal.AsSpan(item.Changes), rlpBehaviors));

    internal static int GetLength(in UInt256 key, ReadOnlySpan<StorageChange> changes, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(key, StorageChangesContentLength(changes, rlpBehaviors)));

    private static int GetContentLength(in UInt256 key, int storageChangesContentLength)
        => Rlp.LengthOf(key) + Rlp.LengthOfSequence(storageChangesContentLength);

    private static int StorageChangesContentLength(ReadOnlySpan<StorageChange> changes, RlpBehaviors rlpBehaviors)
    {
        int len = 0;
        foreach (StorageChange c in changes)
        {
            len += StorageChangeDecoder.Instance.GetLength(c, rlpBehaviors);
        }
        return len;
    }

    internal static void Encode<TWriter>(ref TWriter writer, in UInt256 key, scoped ReadOnlySpan<StorageChange> changes, RlpBehaviors rlpBehaviors)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int storageChangesContentLength = StorageChangesContentLength(changes, rlpBehaviors);
        writer.StartSequence(GetContentLength(key, storageChangesContentLength));
        writer.Encode(key);
        writer.StartSequence(storageChangesContentLength);
        foreach (StorageChange c in changes) StorageChangeDecoder.Instance.Encode(ref writer, c, rlpBehaviors);
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmptyStorageChanges() =>
        throw new RlpException("Empty storage_changes for slot; slot with no changes belongs in storage_reads.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageChangesOutOfOrder(uint index, uint lastIndex) =>
        throw new RlpException($"Storage changes were in incorrect order. index={index}, lastIndex={lastIndex}");
}
