// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Hsst.TwoByteSlot;

/// <summary>
/// N-way merge driver that emits a single TwoByteSlot HSST
/// (<see cref="IndexType.TwoByteSlotValue"/> or
/// <see cref="IndexType.TwoByteSlotValueLarge"/>, picked by total payload size)
/// from N pre-positioned 2-byte-key source enumerators. Drives a
/// <see cref="NWayMergeCursor{TReader,TPin,TSource}"/> over the sources;
/// newest-wins on key collision via the cursor's hardcoded tie-break.
/// </summary>
/// <remarks>
/// Format selection requires the total payload size up front, so the merger
/// stages merged keys/values/lens in the caller-supplied scratch lists before
/// emitting. Scratch lists are <c>Clear()</c>ed on entry; callers can pool
/// them across many merges in a single outer pass (e.g. per-outer-key inside
/// a slot-prefix value merger). Generic over <typeparamref name="TCallback"/>
/// so callers can plug in a per-key hook (e.g. bloom-filter maintenance)
/// without re-iterating the output — pass <see cref="NoOpHsstTwoByteSlotMergeCallback"/>
/// when no hook is needed.
/// </remarks>
internal static class HsstTwoByteSlotMerger
{
    /// <param name="writer">Destination writer; receives one TwoByteSlot HSST blob.</param>
    /// <param name="cursor">Caller-constructed merge cursor over N pre-positioned sources
    /// at 2-byte keys. The merger drives it to exhaustion.</param>
    /// <param name="scratchKeys">Caller-owned scratch for staged 2-byte keys.</param>
    /// <param name="scratchValues">Caller-owned scratch for staged value bytes.</param>
    /// <param name="scratchLens">Caller-owned scratch for per-entry value lengths.</param>
    /// <param name="callback">Per-emitted-key hook; pass
    /// <see cref="NoOpHsstTwoByteSlotMergeCallback"/> when no hook is needed.</param>
    internal static void NWayMerge<TWriter, TReader, TPin, TSource, TCallback>(
        ref TWriter writer,
        scoped ref NWayMergeCursor<TReader, TPin, TSource> cursor,
        ArrayPoolList<byte> scratchKeys,
        ArrayPoolList<byte> scratchValues,
        ArrayPoolList<int> scratchLens,
        TCallback callback)
        where TWriter : IByteBufferWriter
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TCallback : struct, IHsstTwoByteSlotMergeCallback
    {
        const int KeyLength = HsstTwoByteSlotValueBuilder<TWriter>.KeyLength;

        scratchKeys.Clear();
        scratchValues.Clear();
        scratchLens.Clear();

        while (cursor.MoveNext())
        {
            Bound vb = cursor.MinValue;
            using TPin valPin = cursor.CreateMinReader().PinBuffer(vb.Offset, vb.Length);
            ReadOnlySpan<byte> key = cursor.MinKey;
            callback.OnKey(key);
            scratchKeys.AddRange(key);
            scratchValues.AddRange(valPin.Buffer);
            scratchLens.Add((int)vb.Length);
            cursor.AdvanceMatching();
        }

        ReadOnlySpan<byte> mergedKeys = scratchKeys.AsSpan();
        ReadOnlySpan<byte> mergedValues = scratchValues.AsSpan();
        ReadOnlySpan<int> mergedLens = scratchLens.AsSpan();

        if (HsstTwoByteSlotValueBuilder<TWriter>.FitsInOffsetWidth(mergedValues.Length))
        {
            using HsstTwoByteSlotValueBuilder<TWriter> builder = new(ref writer);
            int valOff = 0;
            for (int i = 0; i < mergedLens.Length; i++)
            {
                builder.Add(mergedKeys.Slice(i * KeyLength, KeyLength),
                            mergedValues.Slice(valOff, mergedLens[i]));
                valOff += mergedLens[i];
            }
            builder.Build();
        }
        else
        {
            using HsstTwoByteSlotValueLargeBuilder<TWriter> builder = new(ref writer);
            int valOff = 0;
            for (int i = 0; i < mergedLens.Length; i++)
            {
                builder.Add(mergedKeys.Slice(i * KeyLength, KeyLength),
                            mergedValues.Slice(valOff, mergedLens[i]));
                valOff += mergedLens[i];
            }
            builder.Build();
        }
    }
}
