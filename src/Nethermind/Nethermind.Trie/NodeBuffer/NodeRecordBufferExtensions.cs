// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Operations on the shared node-record representation, independent of buffer storage policy.</summary>
public static partial class NodeRecordBufferExtensions
{
    /// <summary>Protects borrowed records for a stack-scoped operation without allocating a lease object.</summary>
    public static NodeRecordBufferReadLease BeginRead(this INodeRecordBuffer buffer)
    {
        buffer.EnterRead();
        return new NodeRecordBufferReadLease(buffer);
    }

    /// <summary>Protects borrowed records across iterator yields and other heap-scoped operations.</summary>
    public static IDisposable BeginReadScope(this INodeRecordBuffer buffer) => new ReadScope(buffer);

    /// <summary>Appends a node's RLP with every child pointer unset.</summary>
    /// <exception cref="TrieException">The RLP is not a valid trie node.</exception>
    internal static ulong AppendRecordFromRlp(this INodeRecordBuffer buffer, Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp)
    {
        using NodeRecordBufferReadLease lease = buffer.BeginRead();
        if (!NodeRecordCodec.TryMeasure(rlp, out NodeRecordKind kind, out ushort hashMask)) ThrowInvalidRlp();

        int length = NodeRecordCodec.Length(BitOperations.PopCount(hashMask), rlp.Length);
        ulong pointer = buffer.Reserve(NodeRecordCodec.AlignedLength(length), out Span<byte> bytes);
        NodeRecordCodec.WriteFromRlp(rlp, kind, hashMask, hash, NodeRecordCodec.NodeKey(address, path), bytes);
        buffer.CompleteWrite(pointer, bytes, stackalloc int[] { 0 });
        return pointer;
    }

    private sealed class ReadScope : IDisposable
    {
        private INodeRecordBuffer? _buffer;

        public ReadScope(INodeRecordBuffer buffer)
        {
            buffer.EnterRead();
            _buffer = buffer;
        }

        public void Dispose() => Interlocked.Exchange(ref _buffer, null)?.ExitRead();
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidRlp() => throw new TrieException("Invalid trie node RLP.");
}
