// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.PackedArray;

/// <summary>
/// N-way merge driver that emits a single <see cref="IndexType.PackedArray"/> HSST from N
/// pre-positioned source enumerators. Drives a <see cref="NWayMergeCursor{TReader,TPin,TSource,TFactory}"/>
/// over the sources, pins each winner's value through the corresponding source's reader, and
/// writes the (key, value) pair into an <see cref="HsstPackedArrayBuilder{TWriter}"/>. Newest
/// source wins on key collision (the cursor's hardcoded tie-break).
/// </summary>
/// <remarks>
/// Generic over <typeparamref name="TCallback"/> so callers (snapshot merger today) can plug
/// in a per-key hook (bloom-filter maintenance) without re-iterating the output.
/// </remarks>
internal static class HsstPackedArrayMerger
{
    /// <param name="writer">Destination writer; receives one PackedArray HSST.</param>
    /// <param name="valueSize">Per-entry value length, in bytes. All merged values must match.</param>
    /// <param name="cursor">Caller-constructed merge cursor over N pre-positioned sources.
    /// The merger drives it to exhaustion; the key length is read from <see cref="NWayMergeCursor{TReader,TPin,TSource,TFactory}.KeyLen"/>.</param>
    internal static void NWayMerge<TWriter, TReader, TPin, TSource, TFactory, TCallback>(
        ref TWriter writer,
        int valueSize,
        scoped ref NWayMergeCursor<TReader, TPin, TSource, TFactory> cursor,
        TCallback callback)
        where TWriter : IByteBufferWriter
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IHsstByteReader<TPin>, allows ref struct
        where TSource : struct, IHsstMergeSource<TReader, TPin>
        where TFactory : struct, IHsstEnumeratorFactory<TReader, TPin>
        where TCallback : struct, IHsstMergeKeyCallback
    {
        using HsstPackedArrayBuilder<TWriter> builder = new(ref writer, cursor.KeyLen, valueSize);

        while (cursor.MoveNext())
        {
            Bound valBound = cursor.MinValue;
            TReader minReader = cursor.CreateMinReader();
            using TPin valPin = minReader.PinBuffer(valBound);
            builder.Add(cursor.MinKey, valPin.Buffer);
            callback.OnKey(cursor.MinKey);
            cursor.AdvanceMatching();
        }

        builder.Build();
    }
}
