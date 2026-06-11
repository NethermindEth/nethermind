// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Reference-type (heap) container for an <see cref="HsstBTreeBuilderBuffers"/>, letting it be
/// held in a non-ref field and reused across many builds. Used by the persisted-snapshot
/// builder/merger and <see cref="HsstBTreeMerger"/> to amortise per-build buffer rentals.
/// </summary>
internal sealed class HsstBTreeBuilderBuffersContainer(int expectedKeyCount = 16) : IDisposable
{
    private HsstBTreeBuilderBuffers _buffers = new(expectedKeyCount);

    /// <summary>The contained buffers, returned by <c>ref</c> into the field.</summary>
    public ref HsstBTreeBuilderBuffers Buffers => ref _buffers;

    public void Dispose() => _buffers.Dispose();
}
