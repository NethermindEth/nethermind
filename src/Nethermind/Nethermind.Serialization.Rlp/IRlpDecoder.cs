// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp;

public interface IRlpDecoder;

/// <remarks>
/// Encoding takes two passes that measure every nested item only once.
/// <see cref="PopulateLength"/> pushes the lengths <see cref="Encode{TWriter}(ref TWriter, T, ref LengthStack, RlpBehaviors)"/>
/// needs in the reverse of the order it pops them, and returns the total encoded length of the item.
/// The caller uses that total to size its own container or buffer;
/// <see cref="Encode{TWriter}(ref TWriter, T, ref LengthStack, RlpBehaviors)"/> then pops the pushed lengths
/// and writes exactly that many bytes at the writer position.
/// Lengths that are cheap to recompute, such as fixed-width fields, are not pushed but are still part of the total.
/// A parent populates its children last to first, so each child's lengths sit on the stack in encode order.
/// </remarks>
public interface IRlpDecoder<T> : IRlpDecoder
{
    int PopulateLength(T item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <param name="writer">A writer with room for the total length <see cref="PopulateLength"/> returned.</param>
    /// <param name="lengths">The stack <see cref="PopulateLength"/> filled.</param>
    void Encode<TWriter>(ref TWriter writer, T item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    Rlp Encode(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Encodes <paramref name="item"/> into a freshly allocated <see cref="byte"/> array,
    /// without wrapping the result in a throwaway <see cref="Rlp"/>.
    /// </summary>
    byte[] EncodeAsBytes(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    Rlp Encode(T?[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null);

    [return: MaybeNull]
    T Decode(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>Decodes an RLP sequence while preserving null elements for compatibility.</summary>
    T?[] DecodeArray(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null);

    /// <summary>Decodes an RLP sequence and rejects null elements.</summary>
    /// <exception cref="RlpException">An element uses the RLP null marker or decodes as null.</exception>
    T[] DecodeNonNullArray(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null);

    [return: MaybeNull]
    T Decode(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    [return: MaybeNull]
    T DecodeComplete(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// </summary>
    [return: MaybeNull]
    T DecodeComplete(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    [return: NotNull]
    T DecodeGuardNotNull(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="context"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    [return: NotNull]
    T DecodeCompleteNotNull(ref RlpReader context, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Decodes instance of <typeparamref name="T"/> from <paramref name="bytes"/>
    /// and verifies that the end of the stream has been reached.
    /// Throws if decoded value is <c>null</c>.
    /// </summary>
    [return: NotNull]
    T DecodeCompleteNotNull(scoped ReadOnlySpan<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None);
}
