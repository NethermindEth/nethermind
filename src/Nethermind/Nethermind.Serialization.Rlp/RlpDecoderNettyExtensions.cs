// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Provides DotNetty-backed RLP encoding helpers for decoders.
/// </summary>
public static class RlpDecoderNettyExtensions
{
    /// <summary>
    /// Encodes <paramref name="item"/> into a new disposable <see cref="NettyRlpStream"/>.
    /// </summary>
    public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpDecoder<T> decoder, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        NettyRlpStream rlpStream;
        if (item is null)
        {
            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
            rlpStream.WriteByte(Rlp.EmptyListByte);
            return rlpStream;
        }

        rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(decoder.GetLength(item, rlpBehaviors)));
        ValueRlpWriter writer = new(rlpStream);
        decoder.Encode(ref writer, item, rlpBehaviors);
        return rlpStream;
    }

    /// <summary>
    /// Encodes <paramref name="item"/> into a new disposable <see cref="NettyRlpStream"/>.
    /// </summary>
    public static NettyRlpStream EncodeToNewNettyStream(this BlockAccessListDecoder decoder, GeneratedBlockAccessList item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        NettyRlpStream rlpStream = new(NethermindBuffers.Default.Buffer(decoder.GetLength(item, rlpBehaviors)));
        ValueRlpWriter writer = new(rlpStream);
        decoder.Encode(ref writer, item, rlpBehaviors);
        return rlpStream;
    }

    /// <summary>
    /// Encodes <paramref name="items"/> into a new disposable <see cref="NettyRlpStream"/>.
    /// </summary>
    public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpDecoder<T> decoder, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        NettyRlpStream rlpStream;
        if (items is null)
        {
            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
            rlpStream.WriteByte(Rlp.EmptyListByte);
            return rlpStream;
        }

        int totalLength = 0;
        for (int i = 0; i < items.Length; i++)
        {
            totalLength += GetNullableLength(decoder, items[i], behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(bufferLength));
        ValueRlpWriter writer = new(rlpStream);
        writer.StartSequence(totalLength);

        for (int i = 0; i < items.Length; i++)
        {
            EncodeNullable(decoder, ref writer, items[i], behaviors);
        }

        return rlpStream;
    }

    /// <summary>
    /// Encodes <paramref name="items"/> into a new disposable <see cref="NettyRlpStream"/>.
    /// </summary>
    public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpDecoder<T> decoder, IList<T?>? items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        NettyRlpStream rlpStream;
        if (items is null)
        {
            rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(1));
            rlpStream.WriteByte(Rlp.EmptyListByte);
            return rlpStream;
        }

        int totalLength = 0;
        for (int i = 0; i < items.Count; i++)
        {
            totalLength += GetNullableLength(decoder, items[i], behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        rlpStream = new NettyRlpStream(NethermindBuffers.Default.Buffer(bufferLength));
        ValueRlpWriter writer = new(rlpStream);
        writer.StartSequence(totalLength);

        for (int i = 0; i < items.Count; i++)
        {
            EncodeNullable(decoder, ref writer, items[i], behaviors);
        }

        return rlpStream;
    }

    /// <summary>
    /// Encodes <paramref name="items"/> into a new disposable <see cref="NettyRlpStream"/>.
    /// </summary>
    public static NettyRlpStream EncodeToNewNettyStream<T>(this IRlpDecoder<T> decoder, in ArrayPoolListRef<T?> items, RlpBehaviors behaviors = RlpBehaviors.None)
    {
        int totalLength = 0;
        for (int i = 0; i < items.Count; i++)
        {
            totalLength += GetNullableLength(decoder, items[i], behaviors);
        }

        int bufferLength = Rlp.LengthOfSequence(totalLength);

        NettyRlpStream rlpStream = new(NethermindBuffers.Default.Buffer(bufferLength));
        ValueRlpWriter writer = new(rlpStream);
        writer.StartSequence(totalLength);

        for (int i = 0; i < items.Count; i++)
        {
            EncodeNullable(decoder, ref writer, items[i], behaviors);
        }

        return rlpStream;
    }

    private static void EncodeNullable<T>(IRlpDecoder<T> decoder, ref ValueRlpWriter writer, T? item, RlpBehaviors behaviors)
    {
        if (item is null)
        {
            writer.WriteByte(Rlp.EmptyListByte);
            return;
        }

        decoder.Encode(ref writer, item, behaviors);
    }

    private static int GetNullableLength<T>(IRlpDecoder<T> decoder, T? item, RlpBehaviors behaviors)
        => item is null ? Rlp.OfEmptyList.Length : decoder.GetLength(item, behaviors);
}
