// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Cursor-based forward enumerator over an HSST scope, optimised for N-way merge.
/// Materialises the offset table for every leaf entry up-front (zero per-entry heap
/// allocations during the merge), then iterates by index. Class-based — not a ref struct —
/// so callers can put many of these into an array and round-robin them in a sort-merge.
///
/// The data span is passed externally to <see cref="MoveNext"/>/<see cref="GetCurrentValue"/>/
/// <see cref="GetCurrentValueBound"/>: the enumerator only stores integer offsets.
/// </summary>
public sealed class HsstMergeEnumerator : IDisposable
{
    // Per-leaf-entry: separator offset+length in data, and metadata/value offset+length
    private readonly (int SepOffset, int SepLength, int MetaOrValOffset, int ValLength)[] _entries;
    private readonly bool _isInline;
    private int _index = -1;

    // Single reusable key buffer
    private readonly byte[] _keyBuffer;
    private int _keyLength;

    public HsstMergeEnumerator(scoped ReadOnlySpan<byte> hsstData, bool isInline, int maxKeyLength = 64)
    {
        _keyBuffer = new byte[maxKeyLength];
        _isInline = isInline;

        if (hsstData.Length < 2)
        {
            _entries = [];
            return;
        }

        HsstIndex rootIndex = HsstIndex.ReadFromEnd(hsstData, hsstData.Length);
        List<(int, int, int, int)> entries = [];
        CollectLeafOffsets(hsstData, rootIndex, entries, _isInline);
        _entries = [.. entries];
    }

    private static void CollectLeafOffsets(ReadOnlySpan<byte> data, HsstIndex index,
        List<(int, int, int, int)> entries, bool isInline)
    {
        if (!index.IsIntermediate)
        {
            for (int i = 0; i < index.EntryCount; i++)
            {
                ReadOnlySpan<byte> sep = index.GetKey(i);
                int sepOffset = SpanOffset(data, sep);
                if (isInline)
                {
                    ReadOnlySpan<byte> val = index.GetValue(i);
                    int valOffset = val.IsEmpty ? 0 : SpanOffset(data, val);
                    entries.Add((sepOffset, sep.Length, valOffset, val.Length));
                }
                else
                {
                    int metaStart = index.GetIntValue(i);
                    entries.Add((sepOffset, sep.Length, metaStart, 0));
                }
            }
        }
        else
        {
            for (int i = 0; i < index.EntryCount; i++)
            {
                int childOffset = index.GetIntValue(i);
                HsstIndex child = HsstIndex.ReadFromEnd(data, childOffset + 1);
                CollectLeafOffsets(data, child, entries, isInline);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SpanOffset(ReadOnlySpan<byte> outer, ReadOnlySpan<byte> inner) =>
        (int)Unsafe.ByteOffset(
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(outer)),
            ref Unsafe.AsRef(in MemoryMarshal.GetReference(inner)));

    /// <summary>
    /// Decode an entry's <c>(remainingKey, value)</c> at <paramref name="metadataStart"/> within
    /// <paramref name="data"/>. Entry format: <c>[Value][ValueLength: LEB128][RemainingKeyLength: LEB128][RemainingKey]</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadEntry(ReadOnlySpan<byte> data, int metadataStart,
        out ReadOnlySpan<byte> remainingKey, out ReadOnlySpan<byte> value)
    {
        int pos = metadataStart;
        int valueLength = Leb128.Read(data, ref pos);
        int keyLength = Leb128.Read(data, ref pos);
        remainingKey = data.Slice(pos, keyLength);
        value = data.Slice(metadataStart - valueLength, valueLength);
    }

    public int Count => _entries.Length;

    public bool MoveNext(ReadOnlySpan<byte> data)
    {
        if (++_index >= _entries.Length) return false;
        (int sepOff, int sepLen, int metaOrValOff, _) = _entries[_index];
        data.Slice(sepOff, sepLen).CopyTo(_keyBuffer.AsSpan());
        if (_isInline)
        {
            _keyLength = sepLen;
        }
        else
        {
            ReadEntry(data, 1 + metaOrValOff, out ReadOnlySpan<byte> remainingKey, out _);
            remainingKey.CopyTo(_keyBuffer.AsSpan(sepLen));
            _keyLength = sepLen + remainingKey.Length;
        }
        return true;
    }

    public ReadOnlySpan<byte> CurrentKey => _keyBuffer.AsSpan(0, _keyLength);

    public ReadOnlySpan<byte> GetCurrentValue(ReadOnlySpan<byte> data)
    {
        (_, _, int metaOrValOff, int valLen) = _entries[_index];
        if (_isInline) return valLen == 0 ? [] : data.Slice(metaOrValOff, valLen);
        ReadEntry(data, 1 + metaOrValOff, out _, out ReadOnlySpan<byte> value);
        return value;
    }

    public (int Offset, int Length) GetCurrentValueBound(ReadOnlySpan<byte> data)
    {
        (_, _, int metaOrValOff, int valLen) = _entries[_index];
        if (_isInline) return (metaOrValOff, valLen);
        int pos = 1 + metaOrValOff;
        int valueLength = Leb128.Read(data, ref pos);
        return (1 + metaOrValOff - valueLength, valueLength);
    }

    public int CurrentMetadataStart => 1 + _entries[_index].MetaOrValOffset;

    public void Dispose() { }
}
