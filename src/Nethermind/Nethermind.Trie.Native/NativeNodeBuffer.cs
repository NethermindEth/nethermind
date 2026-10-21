// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>
/// Decoded trie nodes native bulk sets wrote, held natively in a buffer they read them back from in place.
/// </summary>
/// <remarks>
/// Each node references its children by hash and by their place in the buffer, and only trie roots are indexed: state roots
/// by hash, storage roots by address and hash. A trie is therefore found by its root and walked in place, so nodes of every
/// branch can be kept without being invalidated on reorgs. Holds nodes in a few small generations, dropping the oldest
/// when a new one starts, which is compacted in the background once no bulk set that started before is still running:
/// only the last node at each path is kept, unless a later generation likely rewrote that path. Compacted generations
/// take about <c>compressedMaxMemory</c> bytes, the oldest being freed beyond it once no running bulk set started before.
/// A <see cref="NativeNodeBufferPin"/> keeps its generation and every later one, so the buffer holds more while pinned.
/// </remarks>
public sealed unsafe partial class NativeNodeBuffer : SafeHandle
{
    /// <summary>The live generations of the buffers that do not take them from configuration.</summary>
    public const int DefaultLiveGenerations = 8;

    // Mirrors the size of the native generation table.
    private const int MaxGenerations = 4096;

    // The overlay's dangerous reference on its parent, which it reads until freed.
    private readonly NativeNodeBuffer? _parent;

    /// <param name="compressedMaxMemory">The memory budget in bytes for compacted generations; it also sizes the storage root index.</param>
    /// <param name="liveGenerations">How many of the newest generations stay readable in place.</param>
    public NativeNodeBuffer(ulong compressedMaxMemory, int liveGenerations) : base(IntPtr.Zero, ownsHandle: true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(liveGenerations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(liveGenerations, MaxGenerations);
        SetHandle(nm_patricia_buffer_new(compressedMaxMemory, (ulong)liveGenerations));
        nm_patricia_buffer_set_memory_pressure(this, &ReportMemoryPressure);
    }

    private NativeNodeBuffer(NativeNodeBuffer parent) : base(IntPtr.Zero, ownsHandle: true)
    {
        bool referenced = false;
        parent.DangerousAddRef(ref referenced);
        _parent = parent;
        SetHandle(nm_patricia_buffer_overlay_new(parent));
        nm_patricia_buffer_set_memory_pressure(this, &ReportMemoryPressure);
    }

    /// <summary>
    /// Creates a buffer that reads the nodes this buffer holds now and keeps the nodes written to it privately, so they never
    /// grow this buffer.
    /// </summary>
    /// <remarks>
    /// The overlay pins the generation active now until it is freed, resolves only the roots indexed before now, and
    /// keeps every node written to it until freed.
    /// </remarks>
    public NativeNodeBuffer CreateOverlay() => new(this);

    // The static entry point outlives native compaction and pins without rooting the SafeHandle against finalization.
    [UnmanagedCallersOnly]
    private static void ReportMemoryPressure(long bytes)
    {
        try
        {
            if (bytes > 0) GC.AddMemoryPressure(bytes);
            else if (bytes < 0) GC.RemoveMemoryPressure(-bytes);
        }
        catch (Exception exception)
        {
            // Managed exceptions must never unwind through native allocation or compactor frames.
            Environment.FailFast("Native trie buffer memory pressure reporting failed", exception);
        }
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        nm_patricia_buffer_free(handle);
        _parent?.DangerousRelease();
        return true;
    }

    /// <summary>
    /// Pins the generation nodes are written to now, so native bulk sets started before the returned pin is disposed
    /// read every node written from now on from this buffer, reached from its trie's root.
    /// </summary>
    public NativeNodeBufferPin PinActiveGeneration()
    {
        bool referenced = false;
        DangerousAddRef(ref referenced);
        ulong boundary;
        ulong generation = nm_patricia_buffer_retain_active(this, &boundary);
        return new NativeNodeBufferPin(this, generation, boundary);
    }

    internal NativeNodeBufferPin Pin(ulong generation, ulong writeBoundary)
    {
        bool referenced = false;
        DangerousAddRef(ref referenced);
        nm_patricia_buffer_retain(this, generation);
        return new NativeNodeBufferPin(this, generation, writeBoundary);
    }

    internal void Unpin(ulong generation)
    {
        // The pin's dangerous reference keeps this raw handle valid even after the owning SafeHandle is closed.
        nm_patricia_buffer_release(handle, generation);
        DangerousRelease();
    }

    /// <summary>The RLP and hash of the node whose record is at <paramref name="pointer"/>.</summary>
    /// <remarks>
    /// The RLP is read in place, so it stays valid only while a <see cref="NativeNodeBufferPin"/> keeps the generation
    /// of <paramref name="pointer"/>, as one taken before the node was written does.
    /// </remarks>
    public ReadOnlySpan<byte> GetNodeRlp(ulong pointer, out ValueHash256 hash)
    {
        ValueHash256 nodeHash = default;
        byte* rlp;
        int length = nm_patricia_buffer_node(this, pointer, &nodeHash, &rlp);
        if (length < 0) throw new InvalidOperationException($"Native trie node buffer read failed with status {length}");

        hash = nodeHash;
        return new ReadOnlySpan<byte>(rlp, length);
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_buffer_new(ulong compressedMaxMemory, ulong liveGenerations);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_buffer_overlay_new(NativeNodeBuffer parent);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_buffer_set_memory_pressure(NativeNodeBuffer buffer, delegate* unmanaged<long, void> report);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial int nm_patricia_buffer_node(NativeNodeBuffer buffer, ulong pointer, ValueHash256* hashOut, byte** rlpOut);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial ulong nm_patricia_buffer_retain_active(NativeNodeBuffer buffer, ulong* writeBoundary);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_buffer_retain(NativeNodeBuffer buffer, ulong generation);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_buffer_release(nint buffer, ulong generation);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial void nm_patricia_buffer_free(nint buffer);
}
