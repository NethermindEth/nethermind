// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp;

public interface IRlpDecoder;

/// <remarks>
/// Encoding takes two passes that measure every nested item only once.
/// <see cref="PopulateLength"/> adds the total encoded length of the item to the writer it is given,
/// begins a container with <see cref="LengthListWriter.BeginContainer"/> for each length
/// <see cref="Encode{TWriter}(ref TWriter, T, ref LengthList, RlpBehaviors)"/> needs, in the order it reads them,
/// and disposes each container once everything in it has been added.
/// <see cref="Encode{TWriter}(ref TWriter, T, ref LengthList, RlpBehaviors)"/> then reads those lengths
/// and writes exactly the populated total at the writer position.
/// Lengths that are cheap to recompute, such as fixed-width fields, take no container but are still added to the total.
/// </remarks>
public interface IRlpDecoder<T> : IRlpDecoder
{
    void PopulateLength(T item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <param name="writer">A writer with room for the total length <see cref="PopulateLength"/> added.</param>
    /// <param name="lengths">The list <see cref="PopulateLength"/> filled, positioned at the first length this item reads.</param>
    void Encode<TWriter>(ref TWriter writer, T item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    Rlp Encode(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None);

    /// <summary>
    /// Encodes <paramref name="item"/> into a freshly allocated <see cref="byte"/> array,
    /// without wrapping the result in a throwaway <see cref="Rlp"/>.
    /// </summary>
    byte[] EncodeAsBytes(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSize = LengthList.DefaultStackallocLength);

    Rlp Encode(T?[]? items, RlpBehaviors rlpBehaviors = RlpBehaviors.None, int stackSizePerItem = 2);

    CappedArray<byte> EncodeToCappedArray(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None, ICappedArrayPool? bufferPool = null, int stackSize = LengthList.DefaultStackallocLength);

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
