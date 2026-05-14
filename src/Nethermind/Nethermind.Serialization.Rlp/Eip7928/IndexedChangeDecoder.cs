// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Serialization.Rlp.Eip7928;

/// <summary>
/// Base class for RLP decoders of <see cref="IIndexedChange"/> types that share the pattern:
/// sequence of (Index, value). Subclasses provide the value field operations.
/// </summary>
public abstract class IndexedChangeDecoder<T> : IRlpValueDecoder<T>, IRlpStreamEncoder<T>
    where T : struct, IIndexedChange
{
    public int GetLength(T item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

    // Eip7928Constants.PrestateIndex (uint.MaxValue) is reserved as a Nethermind-internal
    // sentinel. EIP-7928 itself doesn't reserve it, so the rejection is purely defensive —
    // applied on both encode and decode to keep the sentinel from ever round-tripping through
    // RLP if a future internal mechanism starts using it.
    public T Decode(ref Rlp.ValueDecoderContext ctx, RlpBehaviors rlpBehaviors)
    {
        int length = ctx.ReadSequenceLength();
        int check = length + ctx.Position;

        T result = DecodeFields(ref ctx);
        if (result.Index == Eip7928Constants.PrestateIndex)
        {
            ThrowReservedIndex();
        }

        if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraBytes))
        {
            ctx.Check(check);
        }

        return result;
    }

    public void Encode(RlpStream stream, T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        if (item.Index == Eip7928Constants.PrestateIndex)
        {
            ThrowReservedIndex();
        }

        // EIP-7928 v5.7.0 widened BlockAccessIndex to uint32 (commit 645099785a).
        stream.StartSequence(GetContentLength(item, rlpBehaviors));
        stream.Encode(item.Index);
        EncodeValue(stream, item);
    }

    public int GetContentLength(T item, RlpBehaviors rlpBehaviors)
        => Rlp.LengthOf(item.Index) + GetValueLength(item);

    /// <summary>Decode Index + value field and return a new T.</summary>
    protected abstract T DecodeFields(ref Rlp.ValueDecoderContext ctx);

    /// <summary>Encode only the value field (Index is handled by the base).</summary>
    protected abstract void EncodeValue(RlpStream stream, T item);

    /// <summary>Return the RLP length of the value field.</summary>
    protected abstract int GetValueLength(T item);

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowReservedIndex() =>
        throw new RlpException($"BlockAccessIndex {Eip7928Constants.PrestateIndex} is a reserved sentinel value.");
}
