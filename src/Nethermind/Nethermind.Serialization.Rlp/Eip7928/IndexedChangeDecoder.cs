// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Serialization.FluentRlp;

namespace Nethermind.Serialization.Rlp.Eip7928;

/// <summary>Reads the fields of an <see cref="IIndexedChange"/> sequence.</summary>
/// <remarks>Static, so that the array reads call it directly for each mode of <see cref="RlpBehaviors.TrustedInput"/>.</remarks>
internal interface IIndexedChangeReader<out T> where T : struct, IIndexedChange
{
    /// <summary>Reads Index + value fields and returns a new <typeparamref name="T"/>.</summary>
    /// <param name="fields">The reader of the change sequence; the caller ends the sequence.</param>
    static abstract T ReadFields<TCanonicalChecks>(ref RlpReader fields) where TCanonicalChecks : struct, IFlag;
}

/// <summary>
/// Base class for RLP decoders of <see cref="IIndexedChange"/> types that share the pattern:
/// sequence of (Index, value). Subclasses provide the value field operations.
/// </summary>
public abstract class IndexedChangeDecoder<T> : RlpDecoder<T>
    where T : struct, IIndexedChange
{
    public override int GetLength(T item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    private protected static T Decode<TReader>(ref RlpReader reader, RlpBehaviors rlpBehaviors) where TReader : IIndexedChangeReader<T>
        => (rlpBehaviors & RlpBehaviors.TrustedInput) != 0
            ? Decode<TReader, OffFlag>(ref reader, rlpBehaviors)
            : Decode<TReader, OnFlag>(ref reader, rlpBehaviors);

    private static T Decode<TReader, TCanonicalChecks>(ref RlpReader reader, RlpBehaviors rlpBehaviors)
        where TReader : IIndexedChangeReader<T>
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader fields = reader.EnterSequence<TCanonicalChecks>(out int length);
        T result = TReader.ReadFields<TCanonicalChecks>(ref fields);
        reader.ExitSequence(in fields, length, rlpBehaviors);
        return result;
    }

    /// <summary>Reads a sequence of changes, each of which ends exactly at its fields.</summary>
    /// <exception cref="RlpLimitException">There are more changes than <paramref name="limit"/> allows.</exception>
    internal static T[] DecodeArray<TReader, TCanonicalChecks>(ref RlpReader reader, RlpLimit limit)
        where TReader : IIndexedChangeReader<T>
        where TCanonicalChecks : struct, IFlag
    {
        RlpReader items = reader.EnterSequence<TCanonicalChecks>(out int itemsLength);
        int count = items.CountItems<TCanonicalChecks>(limit);
        T[] result = count == 0 ? [] : new T[count];
        for (int i = 0; i < result.Length; i++)
        {
            RlpReader fields = items.EnterSequence<TCanonicalChecks>(out int length);
            result[i] = TReader.ReadFields<TCanonicalChecks>(ref fields);
            items.ExitSequence(in fields, length);
        }

        reader.ExitSequence(in items, itemsLength);
        return result;
    }

    public override void Encode(ref RlpWriter writer, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        // EIP-7928 v5.7.0 widened BlockAccessIndex to uint32 (commit 645099785a).
        RlpSequence sequence = writer.StartSequence(GetContentLength(item, rlpBehaviors));
        writer.Encode(item.Index);
        EncodeValue(ref writer, item);
        writer.EndSequence(sequence);
    }

    public int GetContentLength(T item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.Index) + GetValueLength(item);

    /// <summary>
    /// Encode only the value field (Index is handled by the base).
    /// </summary>
    protected abstract void EncodeValue(ref RlpWriter writer, T item);

    /// <summary>
    /// Return the RLP length of the value field.
    /// </summary>
    protected abstract int GetValueLength(T item);
}
