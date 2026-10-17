// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>A node a native bulk set wrote, whose RLP is <see cref="RlpLength"/> bytes at <see cref="RlpOffset"/> of the view's arena.</summary>
/// <remarks>Laid out as the native <c>WrittenNode</c>, whose <c>TreePath</c> takes 40 bytes.</remarks>
[StructLayout(LayoutKind.Explicit, Size = 88)]
public readonly struct NativeWrittenNode
{
    [field: FieldOffset(0)] public TreePath Path { get; }
    [field: FieldOffset(40)] public ValueHash256 Hash { get; }
    [field: FieldOffset(72)] public int RlpOffset { get; }
    [field: FieldOffset(76)] public int RlpLength { get; }

    /// <summary>Where the node's record is in the <see cref="NativeTrieNodeLog"/> given to the bulk set, meaningless when it was given none.</summary>
    [field: FieldOffset(80)] public ulong LogPointer { get; }
}

/// <summary>The nodes held by <see cref="NativePendingNodes"/>, valid until its next bulk set, clear or dispose.</summary>
public readonly unsafe struct NativePendingNodesView(NativeWrittenNode* nodes, byte* rlp, int count)
{
    public int Count => count;

    public ref readonly NativeWrittenNode this[int index] => ref new ReadOnlySpan<NativeWrittenNode>(nodes, count)[index];

    public ReadOnlySpan<byte> GetRlp(in NativeWrittenNode node) => new(rlp + node.RlpOffset, node.RlpLength);
}

/// <summary>
/// The trie nodes native bulk sets wrote, one per path with the last write winning, held natively until the caller
/// publishes them. Later bulk sets given the same instance read them back.
/// </summary>
/// <remarks>Disposing gives the native buffers back to their pool, as the finalizer does for an instance never disposed.</remarks>
public sealed unsafe partial class NativePendingNodes : SafeHandle
{
    private NativePendingNodes() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Takes empty pending nodes sized for a bulk set of about <paramref name="estimatedEntries"/> entries.</summary>
    public static NativePendingNodes Rent(int estimatedEntries)
    {
        NativePendingNodes pending = new();
        pending.SetHandle(nm_patricia_pending_rent(estimatedEntries));
        return pending;
    }

    public int Count => nm_patricia_pending_count(this);

    /// <summary>How many shards the nodes are split into by path, more for larger bulk sets, each with its own view.</summary>
    public int ShardCount => nm_patricia_pending_shard_count(this);

    public void Clear() => nm_patricia_pending_clear(this);

    public NativePendingNodesView GetView(int shard)
    {
        NativeWrittenNode* nodes;
        byte* rlp;
        int count = nm_patricia_pending_view(this, shard, &nodes, &rlp);
        return new NativePendingNodesView(nodes, rlp, count);
    }

    protected override bool ReleaseHandle()
    {
        nm_patricia_pending_return(handle);
        return true;
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_pending_rent(int estimatedEntries);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_pending_return(nint pending);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial int nm_patricia_pending_count(NativePendingNodes pending);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_pending_clear(NativePendingNodes pending);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial int nm_patricia_pending_shard_count(NativePendingNodes pending);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial int nm_patricia_pending_view(NativePendingNodes pending, int shard, NativeWrittenNode** nodes, byte** rlp);
}
