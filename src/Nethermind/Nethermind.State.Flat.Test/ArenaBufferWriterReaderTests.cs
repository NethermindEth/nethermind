// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using FluentAssertions;
using Nethermind.State.Flat.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Behaviour of <see cref="ArenaBufferWriter.OpenReader"/>: the buffer-backed
/// fast path (no flush, no mmap when the requested trailing window still sits
/// in the unflushed buffer), the mmap slow path when it doesn't, the post-
/// release flush threshold, the single-active-reader contract, and the
/// promote-on-overflow path when writes during a buffer-backed reader's
/// lifetime would overflow the pinned buffer.
/// </summary>
public class ArenaBufferWriterReaderTests
{
    private const int BufferSize = 1024 * 1024; // mirrors ArenaBufferWriter.BufferSize
    private string _tmpDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"nm_arenawriter_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }

    [Test]
    public unsafe void OpenReader_PastSizeFitsBuffer_ReturnsBufferBackedReader_NoFlush()
    {
        using FileStream fs = NewFile();
        ArenaBufferWriter writer = new(fs, firstOffset: 0,
            (_, _) => throw new InvalidOperationException("OpenView must not be called on the fast path"));
        try
        {
            byte[] payload = MakePattern(8 * 1024);
            WriteAll(ref writer, payload);

            fs.Position.Should().Be(0, "no flush yet");

            ArenaBufferReader reader = writer.OpenReader(payload.Length);
            fs.Position.Should().Be(0, "buffer-backed reader must not flush");

            ReadAndAssert(reader, payload);

            writer.DisposeActiveReader();
            // Buffered bytes are still under the 3/4 threshold so dispose should not flush either.
            fs.Position.Should().Be(0);
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public unsafe void OpenReader_PastSizeExceedsBuffer_TakesMmapPath()
    {
        using FileStream fs = NewFile();
        int openViewCalls = 0;
        long lastOpenViewOffset = -1;
        long lastOpenViewSize = -1;
        ArenaBufferWriter writer = new(fs, firstOffset: 0,
            (relOffset, size) =>
            {
                openViewCalls++;
                lastOpenViewOffset = relOffset;
                lastOpenViewSize = size;
                return OpenFileView(fs, relOffset, size);
            });
        try
        {
            // Write 1.5 MiB — the second half forces an inline Flush() of the first
            // BufferSize bytes during the write, so by the time we OpenReader the
            // first chunk has already been moved into the underlying file.
            byte[] payload = MakePattern(BufferSize + BufferSize / 2);
            WriteAll(ref writer, payload);

            fs.Position.Should().Be(BufferSize, "second-half write must have flushed the first 1 MiB");

            // Ask for the full trailing region — straddles already-flushed bytes,
            // so the writer must take the mmap path.
            ArenaBufferReader reader = writer.OpenReader(payload.Length);

            openViewCalls.Should().Be(1);
            lastOpenViewOffset.Should().Be(0);
            lastOpenViewSize.Should().Be(payload.Length);
            fs.Position.Should().Be(payload.Length, "slow path must Flush()");

            ReadAndAssert(reader, payload);

            writer.DisposeActiveReader();
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public unsafe void DisposeActiveReader_BufferUnderThreshold_DoesNotFlush_OverThreshold_Flushes()
    {
        // Under threshold (< 3/4 of BufferSize) — dispose must keep bytes in buffer.
        using (FileStream fs = NewFile())
        {
            ArenaBufferWriter writer = new(fs, firstOffset: 0,
                (_, _) => throw new InvalidOperationException("fast path expected"));
            try
            {
                int under = (BufferSize / 4) * 3 - 1;
                byte[] payload = MakePattern(under);
                WriteAll(ref writer, payload);

                ArenaBufferReader reader = writer.OpenReader(64);
                ReadOnlySpan<byte> tail = payload.AsSpan(payload.Length - 64);
                ReadAndAssert(reader, tail.ToArray());

                writer.DisposeActiveReader();
                fs.Position.Should().Be(0, "buffered < 3/4 of buffer — dispose must not flush");
            }
            finally { writer.Dispose(); }
        }

        // Over threshold (>= 3/4 of BufferSize) — dispose must flush.
        using (FileStream fs = NewFile())
        {
            ArenaBufferWriter writer = new(fs, firstOffset: 0,
                (_, _) => throw new InvalidOperationException("fast path expected"));
            try
            {
                int over = (BufferSize / 4) * 3 + 1;
                byte[] payload = MakePattern(over);
                WriteAll(ref writer, payload);

                ArenaBufferReader reader = writer.OpenReader(64);
                ReadOnlySpan<byte> tail = payload.AsSpan(payload.Length - 64);
                ReadAndAssert(reader, tail.ToArray());

                writer.DisposeActiveReader();
                fs.Position.Should().Be(over, "buffered >= 3/4 of buffer — dispose must flush");
            }
            finally { writer.Dispose(); }
        }
    }

    [Test]
    public unsafe void OpenReader_SecondCallWhileReaderActive_Throws()
    {
        using FileStream fs = NewFile();
        ArenaBufferWriter writer = new(fs, firstOffset: 0,
            (_, _) => throw new InvalidOperationException("fast path expected"));
        try
        {
            byte[] payload = MakePattern(1024);
            WriteAll(ref writer, payload);

            _ = writer.OpenReader(512);
            Action second = () => writer.OpenReader(256);
            second.Should().Throw<InvalidOperationException>();

            writer.DisposeActiveReader();
        }
        finally { writer.Dispose(); }
    }

    [Test]
    public unsafe void GetSpan_OverflowDuringBufferBackedReader_PromotesToNewBuffer()
    {
        using FileStream fs = NewFile();
        ArenaBufferWriter writer = new(fs, firstOffset: 0,
            (_, _) => throw new InvalidOperationException("buffer-backed reader expected"));
        try
        {
            // Pre-write: a small "data section" we OpenReader on, preceded by
            // exactly enough filler that the buffer is full at OpenReader time
            // (no headroom — the first post-OpenReader write must trigger
            // promote-on-overflow on its first byte).
            int dataSection = 4 * 1024;
            int filler = BufferSize - dataSection;
            byte[] fillerBytes = MakePattern(filler, seed: 0x10);
            byte[] dataBytes = MakePattern(dataSection, seed: 0x20);

            WriteAll(ref writer, fillerBytes);
            WriteAll(ref writer, dataBytes);
            fs.Position.Should().Be(0, "buffer is just full, no write-trigger Flush yet");

            // OpenReader on the tail data section: fast path, pins the buffer.
            ArenaBufferReader reader = writer.OpenReader(dataSection);
            fs.Position.Should().Be(0, "fast path must not flush");
            ReadAndAssert(reader, dataBytes);

            // Next write has zero headroom: must promote. The pinned buffer
            // (filler + data) goes through to the stream; a fresh buffer is
            // rented for the new writes.
            byte[] postBytes = MakePattern(32 * 1024, seed: 0x30);
            WriteAll(ref writer, postBytes);

            fs.Position.Should().Be(BufferSize, "promote flushed exactly the pinned buffer");

            // The reader must still see the original data-section bytes — the
            // pinned buffer is intact even though further writes moved elsewhere.
            ReadAndAssert(reader, dataBytes);

            writer.DisposeActiveReader();

            writer.Flush();
            fs.Position.Should().Be((long)BufferSize + postBytes.Length);

            // Round-trip: the stream contents are filler ++ data ++ post.
            fs.Flush();
            fs.Position = 0;
            byte[] full = new byte[BufferSize + postBytes.Length];
            int got = fs.Read(full, 0, full.Length);
            got.Should().Be(full.Length);
            full.AsSpan(0, filler).SequenceEqual(fillerBytes).Should().BeTrue();
            full.AsSpan(filler, dataSection).SequenceEqual(dataBytes).Should().BeTrue();
            full.AsSpan(filler + dataSection, postBytes.Length).SequenceEqual(postBytes).Should().BeTrue();
        }
        finally { writer.Dispose(); }
    }

    // ---------------- helpers ----------------

    private FileStream NewFile() =>
        new(Path.Combine(_tmpDir, $"f_{Guid.NewGuid():N}.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1);

    private static byte[] MakePattern(int size, byte seed = 0x01)
    {
        byte[] b = new byte[size];
        byte v = seed;
        for (int i = 0; i < size; i++) { b[i] = v; unchecked { v = (byte)(v * 31 + 7); } }
        return b;
    }

    private static void WriteAll(ref ArenaBufferWriter writer, ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> remaining = data;
        while (!remaining.IsEmpty)
        {
            Span<byte> dst = writer.GetSpan(1);
            int n = Math.Min(dst.Length, remaining.Length);
            remaining[..n].CopyTo(dst);
            writer.Advance(n);
            remaining = remaining[n..];
        }
    }

    private static unsafe void ReadAndAssert(ArenaBufferReader reader, ReadOnlySpan<byte> expected)
    {
        reader.Length.Should().Be(expected.Length);
        byte[] actual = new byte[expected.Length];
        reader.TryRead(0, actual).Should().BeTrue();
        actual.AsSpan().SequenceEqual(expected).Should().BeTrue();
    }

    private static unsafe IArenaWholeView OpenFileView(FileStream fs, long offset, long size)
    {
        MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            fs, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(offset, size, MemoryMappedFileAccess.Read);
        byte* ptr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        return new TestFileView(mmf, accessor, ptr + accessor.PointerOffset, size);
    }

    private sealed unsafe class TestFileView(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        byte* dataPtr,
        long size) : IArenaWholeView
    {
        public byte* DataPtr => dataPtr;
        public long Size => size;
        public ReadOnlySpan<byte> GetSpan() => new(dataPtr, checked((int)size));
        public void Dispose()
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            mmf.Dispose();
        }
    }
}
