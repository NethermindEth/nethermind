// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.History.Proofs;

internal enum WindowKind : byte
{
    Unknown,
    Branch,
    Whole,
    Empty,
}

[StructLayout(LayoutKind.Sequential, Size = WindowSlab.HeaderSize)]
internal struct WindowHeader
{
    public ulong LastBlock;
    public int WholeLength;
    public ushort Presence;
    public ushort Changed;
    public WindowKind Kind;
}

internal sealed class WindowSlab : IDisposable
{
    public const int HeaderSize = 24;
    private const int AreaSize = ChildVector.SerializedLength;
    private const int SlotSize = HeaderSize + AreaSize;
    private const int ChunkSize = 1 << 20;
    private const int SlotsPerChunk = ChunkSize / SlotSize;

    private readonly ArrayPoolList<byte[]> _chunks = new(4);
    private readonly Dictionary<int, byte[]> _oversizedWholes = [];
    private int _count;
    private int _previousCount;

    public int ChunkCount => _chunks.Count;

    public int Allocate()
    {
        int slot = _count;
        if (slot / SlotsPerChunk == _chunks.Count) _chunks.Add(ArrayPool<byte>.Shared.Rent(ChunkSize));

        _count++;
        Header(slot) = default;
        return slot;
    }

    public ref WindowHeader Header(int slot) => ref MemoryMarshal.AsRef<WindowHeader>(Slot(slot)[..HeaderSize]);

    public void SetEmpty(int slot)
    {
        ref WindowHeader header = ref Header(slot);
        header.Kind = WindowKind.Empty;
        header.Presence = 0;
        header.WholeLength = 0;
    }

    public void SetWhole(int slot, ReadOnlySpan<byte> rlp)
    {
        ref WindowHeader header = ref Header(slot);
        header.Kind = WindowKind.Whole;
        header.Presence = 0;
        header.WholeLength = rlp.Length;
        if (rlp.Length <= AreaSize)
        {
            rlp.CopyTo(Area(slot));
            return;
        }

        if (!_oversizedWholes.TryGetValue(slot, out byte[]? whole) || whole.Length < rlp.Length)
        {
            if (whole is not null) ArrayPool<byte>.Shared.Return(whole);
            whole = ArrayPool<byte>.Shared.Rent(rlp.Length);
            _oversizedWholes[slot] = whole;
        }

        rlp.CopyTo(whole);
    }

    public ReadOnlySpan<byte> Whole(int slot)
    {
        int length = Header(slot).WholeLength;
        return length <= AreaSize ? Area(slot)[..length] : _oversizedWholes[slot].AsSpan(0, length);
    }

    public void SetBranch(int slot, ChildVector children, ushort presence, ushort changed)
    {
        ref WindowHeader header = ref Header(slot);
        header.Kind = WindowKind.Branch;
        header.WholeLength = 0;
        header.Presence = presence;
        header.Changed |= changed;
        children.WriteTo(Area(slot));
    }

    public void ReadLatest(int slot, ChildVector destination) => destination.ReadFrom(Area(slot));

    public void Reset()
    {
        foreach (byte[] whole in _oversizedWholes.Values) ArrayPool<byte>.Shared.Return(whole);
        _oversizedWholes.Clear();
        int keep = ChunksFor(Math.Max(_count, _previousCount));
        for (int chunk = keep; chunk < _chunks.Count; chunk++) ArrayPool<byte>.Shared.Return(_chunks[chunk]);
        _chunks.Truncate(keep);
        _previousCount = _count;
        _count = 0;
    }

    public void Dispose()
    {
        Reset();
        foreach (byte[] chunk in _chunks) ArrayPool<byte>.Shared.Return(chunk);
        _chunks.Dispose();
    }

    private static int ChunksFor(int slots) => (slots + SlotsPerChunk - 1) / SlotsPerChunk;

    private Span<byte> Slot(int slot) => _chunks[slot / SlotsPerChunk].AsSpan(slot % SlotsPerChunk * SlotSize, SlotSize);

    private Span<byte> Area(int slot) => Slot(slot)[HeaderSize..];
}
