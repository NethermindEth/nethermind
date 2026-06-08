// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Reusable working buffers for <see cref="HsstPartitionedBTreeBuilder{TWriter,TReader,TPin}"/>.
/// Declare one in an outer scope (via <see cref="HsstPartitionedBTreeBuilderBuffersContainer"/>)
/// and reuse it across many builds to amortise the native-list allocations.
/// </summary>
/// <remarks>
/// <see cref="Inner"/> backs the per-partition inner B-tree builds and the final directory
/// build (used strictly sequentially, so one instance suffices). <see cref="AccumHashes"/> /
/// <see cref="AccumOffsets"/> hold the current partition's (key-hash, entry-offset) pairs for
/// hashtable emission. <see cref="DirKeys"/> / <see cref="DirValues"/> / <see cref="DirValueLengths"/>
/// accumulate the directory entries (first key + metadata record) across all partitions.
/// </remarks>
public struct HsstPartitionedBTreeBuilderBuffers : IDisposable
{
    // new(16) (not new()) so the primary constructor runs the inner buffers' field
    // initializers; new() would bind the zero-init parameterless struct ctor and leave
    // the native lists null (NRE on Dispose).
    internal HsstBTreeBuilderBuffers Inner = new(16);
    internal NativeMemoryList<ulong> AccumHashes = new(64);
    internal NativeMemoryList<long> AccumOffsets = new(64);
    internal NativeMemoryList<byte> DirKeys = new(64);
    internal NativeMemoryList<byte> DirValues = new(256);
    internal NativeMemoryList<int> DirValueLengths = new(16);

    // Holds the most-recently-closed partition's inner-root common-prefix bytes, so the
    // single-partition fast path in Build() can append a plain 0x07 trailer.
    internal byte[] RootPrefixScratch = new byte[256];

    public HsstPartitionedBTreeBuilderBuffers() { }

    internal void ResetForBuild()
    {
        AccumHashes.Clear();
        AccumOffsets.Clear();
        DirKeys.Clear();
        DirValues.Clear();
        DirValueLengths.Clear();
    }

    public void Dispose()
    {
        Inner.Dispose();
        AccumHashes.Dispose();
        AccumOffsets.Dispose();
        DirKeys.Dispose();
        DirValues.Dispose();
        DirValueLengths.Dispose();
    }
}

/// <summary>
/// Heap-owning handle for an <see cref="HsstPartitionedBTreeBuilderBuffers"/>, mirroring
/// <see cref="HsstBTreeBuilderBuffersContainer"/>: the ref property lets the buffers be
/// handed to the partitioned builder's borrowed-buffers constructor.
/// </summary>
internal sealed class HsstPartitionedBTreeBuilderBuffersContainer : IDisposable
{
    private HsstPartitionedBTreeBuilderBuffers _buffers = new();

    public ref HsstPartitionedBTreeBuilderBuffers Buffers => ref _buffers;

    public void Dispose() => _buffers.Dispose();
}
