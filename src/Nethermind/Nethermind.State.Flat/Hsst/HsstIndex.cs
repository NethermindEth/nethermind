// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.BSearchIndex;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Thin wrapper around <see cref="BSearchIndexReader"/> that preserves the HsstIndex public API.
/// </summary>
public readonly ref struct HsstIndex
{
    private readonly BSearchIndexReader _inner;

    private HsstIndex(BSearchIndexReader inner) => _inner = inner;

    public int EntryCount => _inner.EntryCount;
    public bool IsIntermediate => _inner.IsIntermediate;
    public BSearchIndexReader.IndexMetadata Metadata => _inner.Metadata;

    public static HsstIndex ReadFromEnd(ReadOnlySpan<byte> data, int indexEnd) =>
        new(BSearchIndexReader.ReadFromEnd(data, indexEnd));

    public ReadOnlySpan<byte> GetKey(int index) => _inner.GetKey(index);
    public ReadOnlySpan<byte> GetValue(int index) => _inner.GetValue(index);
    public int GetIntValue(int index) => _inner.GetIntValue(index);
    public int FindFloorIndex(ReadOnlySpan<byte> key) => _inner.FindFloorIndex(key);

    public bool TryGetFloor(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> floorKey, out ReadOnlySpan<byte> floorValue) =>
        _inner.TryGetFloor(key, out floorKey, out floorValue);

    public BSearchIndexReader.Enumerator GetEnumerator() => _inner.GetEnumerator();
}
