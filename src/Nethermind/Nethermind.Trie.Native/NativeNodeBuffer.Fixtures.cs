// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

[assembly: InternalsVisibleTo("Nethermind.State.Flat.Test")]

namespace Nethermind.Trie.Native;

public sealed partial class NativeNodeBuffer
{
    internal unsafe NativeNodeBuffer(ulong compressedMaxMemory, int generationBytes, int liveGenerations) : base(IntPtr.Zero, ownsHandle: true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(liveGenerations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(liveGenerations, MaxGenerations);
        SetHandle(nm_patricia_buffer_fixture_new(compressedMaxMemory, checked((nuint)generationBytes), (ulong)liveGenerations));
        nm_patricia_buffer_set_memory_pressure(this, &ReportMemoryPressure);
    }

    // The input spans are pinned, and the SafeHandle keeps the buffer alive while Rust decodes and copies the RLP.
    internal unsafe ulong AppendRlp(Hash256? address, in TreePath path, ReadOnlySpan<byte> rlp,
        ReadOnlySpan<ulong> childPointers = default, bool indexRoot = false)
    {
        if (!childPointers.IsEmpty && childPointers.Length != 16)
            throw new ArgumentException("Fixture child pointers must contain 16 slots.", nameof(childPointers));
        ValueHash256 addressHash = address?.ValueHash256 ?? default;
        ulong pointer = 0;
        fixed (TreePath* pathPointer = &path)
        fixed (byte* bytes = rlp)
        fixed (ulong* children = childPointers)
        {
            int status = nm_patricia_buffer_fixture_append_rlp(this, address is null ? null : &addressHash,
                pathPointer, bytes, (nuint)rlp.Length, children, indexRoot, &pointer);
            if (status != 0) throw new ArgumentException($"Invalid native fixture RLP: status {status}", nameof(rlp));
        }
        return pointer;
    }

    internal FixtureRecord GetRecord(ulong pointer) => TryLocate(pointer, out FixtureRecord record)
        ? record : throw new InvalidOperationException($"Native fixture record {pointer} is unavailable");

    // The native call copies under its own read session; this managed record does not borrow native storage.
    internal unsafe bool TryLocate(ulong pointer, out FixtureRecord record)
    {
        int length = nm_patricia_buffer_fixture_copy(this, pointer, null, 0);
        if (length < 0) throw new InvalidOperationException($"Native fixture read failed with status {length}");
        if (length == 0)
        {
            record = default;
            return false;
        }
        byte[] bytes = new byte[length];
        fixed (byte* output = bytes)
        {
            length = nm_patricia_buffer_fixture_copy(this, pointer, output, (nuint)bytes.Length);
        }
        if (length != bytes.Length) throw new InvalidOperationException("Native fixture record expired during its copy");
        record = new FixtureRecord(bytes);
        return true;
    }

    internal readonly struct FixtureRecord(byte[] bytes)
    {
        internal ValueHash256 Hash => new(bytes.AsSpan(8, ValueHash256.MemorySize));
        internal ReadOnlySpan<byte> Rlp => bytes.AsSpan(
            48 + 8 * BitOperations.PopCount(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2))),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));

        internal ulong ChildPointer(int index)
        {
            ushort mask = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
            if ((mask & (1 << index)) == 0) return 0;
            int packedIndex = BitOperations.PopCount((uint)(mask & ((1 << index) - 1)));
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(48 + 8 * packedIndex));
        }
    }

    internal bool IsReadable(ulong pointer) => TryLocate(pointer, out _);

    internal bool IsLive(ulong pointer) => nm_patricia_buffer_fixture_live(this, pointer);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static partial nint nm_patricia_buffer_fixture_new(ulong compressedMaxMemory, nuint generationBytes, ulong liveGenerations);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static unsafe partial int nm_patricia_buffer_fixture_append_rlp(NativeNodeBuffer buffer, ValueHash256* address,
        TreePath* path, byte* rlp, nuint length, ulong* children, [MarshalAs(UnmanagedType.U1)] bool indexRoot, ulong* pointer);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static unsafe partial int nm_patricia_buffer_fixture_copy(NativeNodeBuffer buffer, ulong pointer, byte* output, nuint capacity);

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool nm_patricia_buffer_fixture_live(NativeNodeBuffer buffer, ulong pointer);
}
