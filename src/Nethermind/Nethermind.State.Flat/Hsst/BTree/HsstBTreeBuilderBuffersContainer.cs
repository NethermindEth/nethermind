// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Class handle to a caller-owned <see cref="HsstBTreeBuilderBuffers"/> instance.
/// Lets the buffers be referenced from regular (non-ref) struct fields — needed because
/// the buffers are a ref struct that can be neither a class field (CS0610) nor a ref
/// field on a non-ref struct (CS9051), and a ref field even on a ref struct can't refer
/// to a ref struct (CS9050).
/// </summary>
/// <remarks>
/// <para>The container does NOT own the buffers — the caller allocates and disposes them on
/// its own stack frame and constructs the container with <c>ref</c> to that local. Lifetime
/// contract: the container must not outlive the referenced buffers (same contract as
/// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/>'s borrowed-buffers constructor;
/// no compiler check, so don't store the container past the buffers' scope).</para>
/// <para>The class itself is tiny (one pointer field) and allocated once per merge.</para>
/// </remarks>
internal sealed unsafe class HsstBTreeBuilderBuffersContainer
{
    private readonly void* _ptr;

    public HsstBTreeBuilderBuffersContainer(ref HsstBTreeBuilderBuffers buffers)
        => _ptr = Unsafe.AsPointer(ref buffers);

    /// <summary>Re-borrows the buffers as a <c>ref</c>. Valid as long as the original
    /// stack-allocated buffers instance is still alive.</summary>
    public ref HsstBTreeBuilderBuffers Buffers => ref Unsafe.AsRef<HsstBTreeBuilderBuffers>(_ptr);
}
