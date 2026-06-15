// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp;

public interface IRlpDecoder;

public interface IRlpDecoder<T> : IRlpDecoder
{
    int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    int GetLength(T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None);

    int GetContentLength(T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None);


    void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    void Encode(ref ValueRlpWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    Rlp Encode(T[] items, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null);

    void Encode(RlpStream stream, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None);

    void Encode(ref ValueRlpWriter writer, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None);



    T Decode(ref ValueRlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    T[] DecodeArray(ref ValueRlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null);

    T Decode(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    T DecodeComplete(ref ValueRlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    T DecodeComplete(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    T DecodeGuardNotNull(ref ValueRlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    T DecodeCompleteNotNull(ref ValueRlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    T DecodeCompleteNotNull(ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
}
