// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp;

public interface IRlpDecoder;

public interface IRlpDecoder<T> : IRlpDecoder
{
    /// <summary>Gets the number of bytes that <see cref="Encode(ref RlpWriter, T, RlpBehaviors)"/> writes for <paramref name="item"/>.</summary>
    int GetLength(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    int GetLength(T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None);

    int GetContentLength(T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None);

    /// <summary>Writes <paramref name="item"/> to <paramref name="writer"/>.</summary>
    /// <remarks>Writes exactly <see cref="GetLength(T, RlpBehaviors)"/> bytes, so that callers can write to an exact-size buffer.</remarks>
    void Encode(ref RlpWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    Rlp Encode(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Encodes <paramref name="item"/> into a freshly allocated <see cref="byte"/> array,
    /// without wrapping the result in a throwaway <see cref="Rlp"/>.
    /// </summary>
    byte[] EncodeAsBytes(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    Rlp Encode(T?[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null);

    void Encode(ref RlpWriter writer, T?[]? items, RlpBehaviors behaviors = RlpBehaviors.None);

    /// <summary>Reads one item from <paramref name="reader"/>, and leaves the bytes after it.</summary>
    [return: MaybeNull]
    T Decode(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>Decodes an RLP sequence while preserving null elements for compatibility.</summary>
    T?[] DecodeArray(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null);

    /// <summary>Decodes an RLP sequence and rejects null elements.</summary>
    /// <exception cref="RlpReaderException">An element uses the RLP null marker or decodes as null.</exception>
    T[] DecodeNonNullArray(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null);

    /// <summary>Decodes one item from the start of <paramref name="bytes"/>, and ignores the bytes after it.</summary>
    [return: MaybeNull]
    T Decode(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="reader"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    [return: MaybeNull]
    T DecodeComplete(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    [return: MaybeNull]
    T DecodeComplete(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    [return: NotNull]
    T DecodeGuardNotNull(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="reader"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    [return: NotNull]
    T DecodeCompleteNotNull(ref RlpReader reader, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    [return: NotNull]
    T DecodeCompleteNotNull(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
}
