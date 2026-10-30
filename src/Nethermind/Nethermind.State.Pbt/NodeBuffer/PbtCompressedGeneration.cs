// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>The records compaction kept of a group record buffer generation, found by their original buffer pointer.</summary>
/// <remarks>
/// Records are copied in buffer order, each 8-aligned, and indexed by an open-addressing table with linear probing, a load factor of at most 0.75
/// and an empty pointer of 0. It is immutable once built, so lookups need no lock.
/// </remarks>
public sealed class PbtCompressedGeneration : IDisposable
{
    private readonly PbtNativeGroupBuffer _records;
    private readonly (ulong Pointer, int Offset)[] _table;
    private readonly ulong _tableMask;

    /// <param name="generation">The records of the generation, in place.</param>
    /// <param name="generationId">The id of the generation, which forms the pointers of its records.</param>
    /// <param name="keptOffsets">The offsets of the records to keep, ascending.</param>
    public PbtCompressedGeneration(PbtNativeGroupBuffer generation, ulong generationId, ReadOnlySpan<int> keptOffsets)
    {
        int length = 0;
        foreach (int offset in keptOffsets)
        {
            length += PbtGroupRecordCodec.AlignedLength(PbtGroupRecordCodec.Length(generation.AsSpan(offset)));
        }

        _table = new (ulong, int)[BitOperations.RoundUpToPowerOf2((uint)(keptOffsets.Length * 4 / 3 + 1))];
        _tableMask = (ulong)_table.Length - 1;
        _records = PbtNativeGroupBuffer.Allocate(length);
        try
        {
            int copied = 0;
            foreach (int offset in keptOffsets)
            {
                ReadOnlySpan<byte> record = generation.AsSpan(offset, PbtGroupRecordCodec.Length(generation.AsSpan(offset)));
                ulong pointer = generationId << PbtGroupRecordCodec.OffsetBits | (uint)offset;
                ulong index = Index(pointer);
                while (_table[index].Pointer != 0) index = (index + 1) & _tableMask;

                _table[index] = (pointer, copied);
                record.CopyTo(_records.AsSpan(copied));
                copied += PbtGroupRecordCodec.AlignedLength(record.Length);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Bytes held by the records and the table.</summary>
    public long Memory => _records.Length + (long)_table.Length * Unsafe.SizeOf<(ulong, int)>();

    public bool TryFind(ulong pointer, out PbtGroupRecord record)
    {
        for (ulong index = Index(pointer); _table[index].Pointer != 0; index = (index + 1) & _tableMask)
        {
            if (_table[index].Pointer != pointer) continue;

            record = new PbtGroupRecord(_records.Memory, _table[index].Offset);
            return true;
        }

        record = default;
        return false;
    }

    public void Dispose() => ((IDisposable)_records).Dispose();

    private ulong Index(ulong pointer) => (pointer * 0x9E3779B97F4A7C15 >> 32) & _tableMask;
}
