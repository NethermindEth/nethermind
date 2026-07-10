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


    void Encode<TWriter>(ref TWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    Rlp Encode(T[] items, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null);

    void Encode<TWriter>(ref TWriter writer, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;



    T Decode(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    T[] DecodeArray(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null);

    T Decode(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    T DecodeComplete(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    T DecodeComplete(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    T DecodeGuardNotNull(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    T DecodeCompleteNotNull(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    T DecodeCompleteNotNull(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
}
