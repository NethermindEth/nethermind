// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Heap-owning handle for an <see cref="HsstBTreeBuilderBuffers"/> instance. Lets the
/// buffers be referenced from regular (non-ref) struct fields that need to outlive a
/// single stack frame — e.g. a value-merger callback that's passed to an N-way merge
/// driver and must amortise the per-build buffer rentals across many emitted entries.
/// </summary>
/// <remarks>
/// <para>The container OWNS the buffers — they live as a field on the class instance and
/// are released by <see cref="Dispose"/>. The <see cref="Buffers"/> ref property returns a
/// real <c>ref</c> into the field, so callers can pass it on to <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/>'s
/// borrowed-buffers constructor without any unsafe pointer laundering.</para>
/// <para>One small heap allocation per container instance.</para>
/// </remarks>
internal sealed class HsstBTreeBuilderBuffersContainer(int expectedKeyCount = 16) : IDisposable
{
    private HsstBTreeBuilderBuffers _buffers = new(expectedKeyCount);

    /// <summary>The contained buffers, returned by ref so callers can hand them to
    /// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/>'s borrowed-buffers constructor
    /// or to helpers that take <c>ref HsstBTreeBuilderBuffers</c>.</summary>
    public ref HsstBTreeBuilderBuffers Buffers => ref _buffers;

    public void Dispose() => _buffers.Dispose();
}
