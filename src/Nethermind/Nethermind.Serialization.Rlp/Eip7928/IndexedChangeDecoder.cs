// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

/// <summary>
/// Base class for RLP decoders of <see cref="IIndexedChange"/> types that share the pattern:
/// sequence of (BlockAccessIndex, value). Subclasses provide the value field operations.
/// </summary>
public abstract class IndexedChangeDecoder<T> : IRlpValueDecoder<T>, IRlpStreamEncoder<T>
    where T : struct, IIndexedChange
{
    public int GetLength(T item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    public T Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        T result = DecodeFields(ref ctx);

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return result;
    }

    public void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.BlockAccessIndex);
        EncodeValue(stream, item);
    }

    public int GetContentLength(T item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.BlockAccessIndex) + GetValueLength(item);

    /// <summary>Decode BlockAccessIndex + value field and return a new T.</summary>
    protected abstract T DecodeFields(ref Rlp.ValueDecoderContext ctx);

    /// <summary>Encode only the value field (BlockAccessIndex is handled by the base).</summary>
    protected abstract void EncodeValue(RlpStream stream, T item);

    /// <summary>Return the RLP length of the value field.</summary>
    protected abstract int GetValueLength(T item);
}
