// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Io;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// Pointer-backed <see cref="IByteReader{TPin}"/> over a base-table arena mmap: the raw mapped region
/// only, with no reservation or residency tracking (base shard tables are pinned for the reader's
/// lifetime by their <see cref="BaseTableView"/> lease).
/// </summary>
internal readonly unsafe struct MmapByteReader(byte* basePtr, long length) : IByteReader<NoOpPin>
{
    public long Length => length;

    public bool TryRead(long offset, scoped Span<byte> output)
    {
        if ((ulong)offset + (ulong)output.Length > (ulong)length) return false;
        // Safety: the bounds check above keeps [offset, offset + output.Length) inside the mapped region
        // [basePtr, basePtr + length), which the caller keeps alive through an ArenaFile lease.
        new ReadOnlySpan<byte>(basePtr + offset, output.Length).CopyTo(output);
        return true;
    }

    public NoOpPin PinBuffer(Bound bound)
    {
        if ((ulong)bound.Offset + (ulong)bound.Length > (ulong)length)
            throw new ArgumentOutOfRangeException(nameof(bound));
        // Safety: same in-bounds guarantee as TryRead; the pinned span never outlives the mapping because
        // the NoOpPin is consumed within the seek/scan while the view lease keeps the file mapped.
        return new NoOpPin(new ReadOnlySpan<byte>(basePtr + bound.Offset, checked((int)bound.Length)));
    }
}
