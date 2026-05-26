// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.PackedArray;

/// <summary>
/// N-way merge driver that emits a single <see cref="IndexType.PackedArray"/> HSST from N
/// pre-positioned source enumerators. Drives a <see cref="NWayMergeCursor{TReader,TPin,TSource}"/>
/// over the sources, pins each winner's value through the corresponding source's reader, and
/// writes the (key, value) pair into an <see cref="HsstPackedArrayBuilder{TWriter}"/>. Newest
/// source wins on key collision (the cursor's hardcoded tie-break).
/// </summary>
/// <remarks>
/// Generic over <typeparamref name="TCallback"/> so callers (snapshot merger today) can plug
/// in a per-key hook (bloom-filter maintenance) without re-iterating the output. Use
/// <see cref="NoOpHsstPackedArrayMergeCallback"/> when no hook is needed.
/// </remarks>
internal static class HsstPackedArrayMerger
{
    /// <param name="writer">Destination writer; receives one PackedArray HSST.</param>
    /// <param name="keySize">Per-entry key length, in bytes. Must match every source's keys
    /// and the cursor's <c>keyLen</c>.</param>
    /// <param name="valueSize">Per-entry value length, in bytes. All merged values must match.</param>
    /// <param name="sources">Pre-positioned source structs, one per cursor slot. Each source's
    /// enumerator has already been <c>MoveNext</c>'d once by the caller; <c>state.HasMore[i]</c>
    /// and <c>state.KeyBuf[i*KeyStride..]</c> are set accordingly.</param>
    /// <param name="state">Caller-allocated loser-tree scratch.</param>
    /// <param name="callback">Per-emitted-key hook; pass <see cref="NoOpHsstPackedArrayMergeCallback"/>
    /// when no hook is needed.</param>
    internal static void NWayMerge<TWriter, TReader, TPin, TSource, TCallback>(
        ref TWriter writer,
        int keySize, int valueSize,
        Span<TSource> sources,
        LoserTreeState state,
        TCallback callback)
        where TWriter : IByteBufferWriter
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TCallback : struct, IHsstPackedArrayMergeCallback
    {
        NWayMergeCursor<TReader, TPin, TSource> cursor = new(sources, state, keySize);
        using HsstPackedArrayBuilder<TWriter> builder = new(ref writer, keySize, valueSize);

        while (cursor.MoveNext())
        {
            int minIdx = cursor.MinIdx;
            HsstEnumerator<TReader, TPin> e = sources[minIdx].GetEnumerator();
            Bound valBound = e.CurrentValue;
            TReader minReader = sources[minIdx].CreateReader();
            using TPin valPin = minReader.PinBuffer(valBound.Offset, valBound.Length);
            builder.Add(cursor.MinKey, valPin.Buffer);
            callback.OnKey(cursor.MinKey);
            cursor.AdvanceMatching();
        }

        builder.Build();
    }
}
