// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

/// <summary>
/// Like TreePath, but tiny. Fit in 8 byte, like a long. Can only represent 14 nibble.
/// </summary>
public readonly struct TinyTreePath : IEquatable<TinyTreePath>
{
    public const int MaxNibbleLength = 14;

    private readonly long _data;

    private ReadOnlySpan<byte> AsSpan => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in _data, 1));

    public TinyTreePath(in TreePath path)
    {
        if (path.Length > MaxNibbleLength) throw new InvalidOperationException("Unable to represent more than 14 nibble");
        Span<byte> pathSpan = path.Path.BytesAsSpan;
        Span<byte> selfSpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _data, 1));
        pathSpan[..7].CopyTo(selfSpan);
        selfSpan[7] = (byte)path.Length;
    }

    public int Length => AsSpan[7];

    public TreePath ToTreePath()
    {
        ValueHash256 rawPath = Keccak.Zero;
        Span<byte> pathSpan = rawPath.BytesAsSpan;
        ReadOnlySpan<byte> selfSpan = AsSpan;
        selfSpan[..7].CopyTo(pathSpan);

        return new TreePath(rawPath, selfSpan[7]);
    }

    public bool Equals(TinyTreePath other) => _data == other._data;
    public override bool Equals(object? obj) => obj is TinyTreePath other && Equals(other);
    public override int GetHashCode() => _data.GetHashCode();
}
