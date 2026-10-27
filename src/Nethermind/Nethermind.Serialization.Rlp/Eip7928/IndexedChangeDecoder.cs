// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

/// <summary>
/// Base class for RLP decoders of <see cref="IIndexedChange"/> types that share the pattern:
/// sequence of (Index, value). Subclasses provide the value field operations.
/// </summary>
public abstract class IndexedChangeDecoder<T> : RlpDecoder<T>
    where T : struct, IIndexedChange
{
    public override void PopulateLength(T item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item, rlpBehaviors);
        lengths.Push(contentLength);
        lengths.Push(Rlp.LengthOfSequence(contentLength));
    }

    protected override T DecodeInternal(ref RlpReader ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        T result = DecodeFields(ref ctx, check);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return result;
    }

    public override void Encode<TWriter>(T item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        // EIP-7928 v5.7.0 widened BlockAccessIndex to uint32 (commit 645099785a).
        writer.StartSequence(lengths.Pop());
        writer.Encode(item.Index);
        EncodeValue(ref writer, item);
    }

    public int GetContentLength(T item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.Index) + GetValueLength(item);

    /// <summary>
    /// Decode Index + value field and return a new T.
    /// </summary>
    protected abstract T DecodeFields(ref RlpReader ctx);

    /// <summary>
    /// Decode the fields of a change whose sequence ends at <paramref name="end"/>, for a change with optional trailing fields.
    /// </summary>
    protected virtual T DecodeFields(ref RlpReader ctx, int end) => DecodeFields(ref ctx);

    /// <summary>
    /// Encode only the value field (Index is handled by the base).
    /// </summary>
    protected abstract void EncodeValue<TWriter>(ref TWriter writer, T item)
        where TWriter : struct, IRlpWriteBackend, allows ref struct;

    /// <summary>
    /// Return the RLP length of the value field.
    /// </summary>
    protected abstract int GetValueLength(T item);
}
