// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Operations on the shared node-record representation, independent of buffer storage policy.</summary>
public static partial class NodeRecordBufferExtensions
{
    /// <summary>The root of <paramref name="rootHash"/>, with its record pointer when the buffer indexes it.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    internal static TrieRoot ResolveRoot(this INodeRecordBuffer buffer, Hash256? address, in ValueHash256 rootHash) =>
        new(rootHash, rootHash == ValueKeccak.EmptyTreeHash ? 0 : buffer.FindRoot(address, rootHash));

    /// <summary>Appends a node's RLP with every child pointer unset.</summary>
    /// <exception cref="TrieException">The RLP is not a valid trie node.</exception>
    internal static ulong AppendRecordFromRlp(this INodeRecordBuffer buffer, Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        if (!NodeRecordCodec.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask)) ThrowInvalidRlp();

        int length = NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length);
        using INodeRecordWriteBatch batch = buffer.StartWriteBatch(NodeRecordCodec.AlignedLength(length));
        NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, hash, NodeRecordCodec.NodeKeyHash(address, path), batch.Bytes);
        return batch.BasePointer;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidRlp() => throw new TrieException("Invalid trie node RLP.");
}
