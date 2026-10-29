// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.NodeBuffer;

internal enum ChildRefKind : byte
{
    Empty,
    Hash,
    Inline,
    Pending,
}

/// <summary>A reference from a node to one of its children: none, a hash, an embedded node, or a node not yet encoded.</summary>
internal struct ChildRef
{
    public ChildRefKind Kind;
    public byte InlineLength;
    public int PendingIndex;

    /// <summary>Buffer pointer of the child's record when <see cref="Kind"/> is <see cref="ChildRefKind.Hash"/>; 0 when unknown.</summary>
    public ulong Pointer;

    /// <summary>The child's hash, or its embedded RLP of <see cref="InlineLength"/> bytes.</summary>
    public ValueHash256 Data;

    public static ChildRef ForHash(in ValueHash256 hash, ulong pointer) => new() { Kind = ChildRefKind.Hash, Pointer = pointer, Data = hash };

    public static ChildRef ForInline(ReadOnlySpan<byte> rlp)
    {
        Debug.Assert(rlp.Length < ValueHash256.MemorySize && NodeRecordCodec.IsInlineLeaf(rlp));
        ChildRef child = new() { Kind = ChildRefKind.Inline, InlineLength = (byte)rlp.Length };
        rlp.CopyTo(child.Data.BytesAsSpan);
        return child;
    }

    public static ChildRef ForPending(int index) => new() { Kind = ChildRefKind.Pending, PendingIndex = index };

    [UnscopedRef]
    public readonly ReadOnlySpan<byte> InlineRlp => Data.Bytes[..InlineLength];

    public readonly int EncodedLength => Kind switch
    {
        ChildRefKind.Empty => 1,
        ChildRefKind.Hash => Rlp.LengthOfKeccakRlp,
        ChildRefKind.Inline => InlineLength,
        _ => ThrowPending(),
    };

    /// <returns>The number of bytes written, <see cref="EncodedLength"/>.</returns>
    public readonly int Write(Span<byte> destination)
    {
        switch (Kind)
        {
            case ChildRefKind.Empty:
                destination[0] = Rlp.EmptyByteArrayByte;
                return 1;
            case ChildRefKind.Hash:
                destination[0] = Rlp.EmptyByteArrayByte + ValueHash256.MemorySize;
                Data.Bytes.CopyTo(destination[1..]);
                return Rlp.LengthOfKeccakRlp;
            case ChildRefKind.Inline:
                InlineRlp.CopyTo(destination);
                return InlineLength;
            default:
                return ThrowPending();
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static int ThrowPending() => throw new TrieException("A pending child must be encoded before its parent.");
}
