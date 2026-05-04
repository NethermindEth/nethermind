// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
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
    // Per-leaf-entry: separator offset+length in data, and metadata/value offset+length.
    // Backed by NativeMemoryList so the per-merge enumerator allocations sit off the managed heap.
    private readonly NativeMemoryList<(int SepOffset, int SepLength, int MetaOrValOffset, int ValLength)> _entries;
    private readonly bool _isInline;
    private int _index = -1;

    // Single reusable key buffer (NativeMemoryList, disposed in Dispose()).
    private readonly NativeMemoryList<byte> _keyBufferList;
    private int _keyLength;
    private bool _disposed;

    public HsstMergeEnumerator(scoped ReadOnlySpan<byte> hsstData, bool isInline, int maxKeyLength = 64)
    {
        _keyBufferList = new NativeMemoryList<byte>(maxKeyLength, maxKeyLength);
        _isInline = isInline;

        if (hsstData.Length < 2)
        {
            _entries = new NativeMemoryList<(int, int, int, int)>(0);
            return;
        }

        HsstIndex rootIndex = HsstIndex.ReadFromEnd(hsstData, hsstData.Length);
        _entries = new NativeMemoryList<(int, int, int, int)>(16);
        CollectLeafOffsets(hsstData, rootIndex, _entries, _isInline);
    }

    private static void CollectLeafOffsets(ReadOnlySpan<byte> data, HsstIndex index,
        NativeMemoryList<(int, int, int, int)> entries, bool isInline)
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
    /// Decode an entry's <c>(fullKey, value)</c> at <paramref name="metadataStart"/> within
    /// <paramref name="data"/>. Entry format: <c>[Value][ValueLength: LEB128][KeyLength: LEB128][FullKey]</c>.
    /// metaStart points at the <c>ValueLength</c> LEB128 (value sits before, lengths + key sit
    /// after) — LEB128 has a forward-only terminator so it can't be reliably read backward.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReadEntry(ReadOnlySpan<byte> data, int metadataStart,
        out ReadOnlySpan<byte> fullKey, out ReadOnlySpan<byte> value)
    {
        int pos = metadataStart;
        int valueLength = Leb128.Read(data, ref pos);
        int keyLength = Leb128.Read(data, ref pos);
        fullKey = data.Slice(pos, keyLength);
        value = data.Slice(metadataStart - valueLength, valueLength);
    }

    public int Count => _entries.Count;

    public bool MoveNext(ReadOnlySpan<byte> data)
    {
        if (++_index >= _entries.Count) return false;
        (int sepOff, int sepLen, int metaOrValOff, _) = _entries[_index];
        if (_isInline)
        {
            // Inline mode: separator IS the full key; copy from the leaf section.
            data.Slice(sepOff, sepLen).CopyTo(_keyBufferList.AsSpan());
            _keyLength = sepLen;
        }
        else
        {
            // Non-inline: data-region entry carries the full key — copy it directly.
            ReadEntry(data, 1 + metaOrValOff, out ReadOnlySpan<byte> fullKey, out _);
            fullKey.CopyTo(_keyBufferList.AsSpan());
            _keyLength = fullKey.Length;
        }
        return true;
    }

    public ReadOnlySpan<byte> CurrentKey => _keyBufferList.AsSpan().Slice(0, _keyLength);

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _entries.Dispose();
        _keyBufferList.Dispose();
    }
}
