// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;

namespace Nethermind.State.Flat.Io;

/// <summary>Read-only file reader that serves every read from whole table blocks it holds in native memory.</summary>
/// <remarks>
/// For standalone files that are not arena-managed — a transient sorted run, for instance. Unlike a mapping, the
/// held blocks are private memory the kernel cannot drop under memory pressure only to fault them back in on the next
/// touch. Two blocks are held, the most recently used, so a table walk keeps both its data block and its index block.
/// The file must not be written or truncated while this instance is alive, and a single instance is not thread-safe.
/// </remarks>
internal sealed unsafe class BlockBufferedFile : IByteReader<NoOpPin>, IDisposable
{
    private const int BlockSize = SortedTable.BlockSize;
    private const long NoBlock = -1;

    private readonly SafeFileHandle _handle;
    private readonly long[] _blockStarts = [NoBlock, NoBlock];
    private byte* _blocks;
    private int _recent;

    public BlockBufferedFile(string path)
    {
        _handle = File.OpenHandle(path);
        Length = RandomAccess.GetLength(_handle);
        if (Length == 0)
        {
            _handle.Dispose();
            throw new InvalidDataException($"Cannot read the empty file {path}.");
        }
        _blocks = (byte*)NativeMemory.Alloc((nuint)(_blockStarts.Length * BlockSize));
    }

    public long Length { get; }

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if (offset < 0 || (ulong)offset + (ulong)output.Length > (ulong)Length) return false;
        while (!output.IsEmpty)
        {
            long blockStart = offset & ~(long)(BlockSize - 1);
            ReadOnlySpan<byte> block = Block(blockStart)[(int)(offset - blockStart)..];
            int count = Math.Min(output.Length, block.Length);
            block[..count].CopyTo(output);
            output = output[count..];
            offset += count;
        }
        return true;
    }

    /// <remarks>The pinned bytes are a copy, as a later read may replace the held block they came from.</remarks>
    public NoOpPin PinBuffer(Bound bound)
    {
        byte[] copy = new byte[checked((int)bound.Length)];
        if (!TryRead(bound.Offset, copy)) throw new ArgumentOutOfRangeException(nameof(bound));
        return new NoOpPin(copy);
    }

    /// <summary>The block at <paramref name="blockStart"/>, read into the least recently used slot unless already held.</summary>
    private ReadOnlySpan<byte> Block(long blockStart)
    {
        int length = (int)Math.Min(BlockSize, Length - blockStart);
        int slot = Array.IndexOf(_blockStarts, blockStart);
        if (slot < 0)
        {
            slot = 1 - _recent;
            // Cleared first, so a failed read leaves no slot claiming bytes it does not hold.
            _blockStarts[slot] = NoBlock;
            Span<byte> target = new(_blocks + slot * BlockSize, length);
            for (int read = 0; read < length;)
            {
                int count = RandomAccess.Read(_handle, target[read..], blockStart + read);
                if (count == 0) throw new EndOfStreamException($"File ended inside the block at {blockStart}.");
                read += count;
            }
            _blockStarts[slot] = blockStart;
        }
        _recent = slot;
        return new ReadOnlySpan<byte>(_blocks + slot * BlockSize, length);
    }

    public void Dispose()
    {
        if (_blocks is null) return;
        NativeMemory.Free(_blocks);
        _blocks = null;
        _handle.Dispose();
    }
}
