// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// A growable, native-memory-backed byte buffer that backs the RAM arena tiers
/// (<see cref="InMemoryArenaManager"/> / <see cref="InMemoryBlobArenaManager"/>) in place of an
/// mmap'd file. Appended into once during a single writer's lifetime, then frozen and read
/// concurrently — the "written once, then read-only" property lets completed slices be spanned /
/// copied without synchronisation. Growth only happens on the exclusive write path, so a
/// <see cref="NativeMemory.Realloc"/> can move the block without racing a reader.
/// </summary>
internal sealed unsafe class NativeArenaBuffer : IDisposable
{
    private byte* _ptr;
    private long _capacity;
    private long _length;

    public NativeArenaBuffer(long initialCapacity)
    {
        _capacity = Math.Max(initialCapacity, 1);
        _ptr = (byte*)NativeMemory.AllocZeroed((nuint)_capacity);
    }

    // Safety net: free the native block if a buffer is leaked without Dispose, so the RAM isn't lost.
    ~NativeArenaBuffer() => FreeBuffer();

    /// <summary>Base pointer to the first byte. Stable once the buffer is frozen (no further appends).</summary>
    public byte* Pointer => _ptr;
    public long Length => _length;
    public long Capacity => _capacity;

    /// <summary>Append <paramref name="data"/> at the current end, growing the block if needed.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        EnsureCapacity(_length + data.Length);
        data.CopyTo(new Span<byte>(_ptr + _length, data.Length));
        _length += data.Length;
    }

    private void EnsureCapacity(long required)
    {
        if (required <= _capacity) return;
        long newCap = _capacity;
        // Grow by doubling, but never below `required` and never overflow long: once doubling would
        // wrap past long.MaxValue, jump straight to `required` (the true minimum).
        while (newCap < required)
        {
            if (newCap > long.MaxValue / 2)
            {
                newCap = required;
                break;
            }
            newCap *= 2;
        }
        _ptr = (byte*)NativeMemory.Realloc(_ptr, (nuint)newCap);
        // Zero the grown tail so any unwritten padding reads back as zero, mirroring the sparse-file
        // zeros the disk arena relies on.
        NativeMemory.Clear(_ptr + _capacity, (nuint)(newCap - _capacity));
        _capacity = newCap;
    }

    /// <summary>Copy up to <paramref name="destination"/>.Length bytes from <paramref name="offset"/>.
    /// Returns the number copied; 0 at or past the written length (short read at EOF).</summary>
    public int ReadAt(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= _length) return 0;
        int count = (int)Math.Min(destination.Length, _length - offset);
        new ReadOnlySpan<byte>(_ptr + offset, count).CopyTo(destination[..count]);
        return count;
    }

    public void Dispose()
    {
        FreeBuffer();
        GC.SuppressFinalize(this);
    }

    // Idempotent, thread-agnostic (NativeMemory.Free needs no managed state) so it is safe to run from
    // either Dispose or the finalizer. Null the pointer before freeing so a double-call can't double-free.
    private void FreeBuffer()
    {
        byte* p = _ptr;
        if (p is not null)
        {
            _ptr = null;
            NativeMemory.Free(p);
        }
    }
}

/// <summary>
/// Append-only writable <see cref="Stream"/> over a <see cref="NativeArenaBuffer"/>, handed to the
/// existing <see cref="ArenaWriter"/> / <see cref="BlobArenaWriter"/> in RAM mode in place of a
/// <see cref="FileStream"/>. It does not own the buffer — the arena file frees it on cleanup.
/// </summary>
internal sealed class NativeArenaWriteStream(NativeArenaBuffer buffer) : Stream
{
    private readonly NativeArenaBuffer _buffer = buffer;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;
    public override long Position { get => _buffer.Length; set => throw new NotSupportedException(); }

    public override void Write(ReadOnlySpan<byte> buffer) => _buffer.Append(buffer);
    public override void Write(byte[] buffer, int offset, int count) => _buffer.Append(buffer.AsSpan(offset, count));

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
