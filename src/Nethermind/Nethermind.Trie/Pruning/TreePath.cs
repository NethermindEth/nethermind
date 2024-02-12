// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

/// <summary>
/// Patricia trie tree path. Can represent up to 64 nibbles in 32+4 byte.
/// Can be used as ref struct, and mutated during trie traversal.
/// </summary>
[Todo("check if its worth it to change the length to byte, or if it actually make things slower.")]
[Todo("check if its worth it to not clear byte during TruncateMut, but will need proper comparator, span copy, etc.")]
public struct TreePath
{
    const int NoopLength = 255; // Length marking that the TreePath is a noop.
    public readonly ValueHash256 Path;

    public static TreePath Empty => new TreePath();
    public static TreePath Noop => Empty;

    private readonly Span<byte> Span => Path.BytesAsSpan;

    public TreePath(in ValueHash256 path, int length)
    {
        Path = path;
        Length = length;
    }

    public int Length { get; private set; }

    public static TreePath FromPath(ReadOnlySpan<byte> pathHash)
    {
        if (pathHash.Length != 32) throw new InvalidOperationException("Path must be 32 byte");
        return new TreePath(new ValueHash256(pathHash), 64);
    }

    public static TreePath FromNibble(ReadOnlySpan<byte> pathNibbles)
    {
        if (pathNibbles.Length > 64) throw new InvalidOperationException($"Nibble length too long: {pathNibbles.Length}. Max is 64");
        ValueHash256 hash = Keccak.Zero;
        ToBytesExtra(pathNibbles, hash.BytesAsSpan);

        return new TreePath(hash, pathNibbles.Length);
    }

    private static void ToBytesExtra(ReadOnlySpan<byte> nibbles, Span<byte> bytes)
    {
        for (int i = 0; i < nibbles.Length / 2; i++)
        {
            bytes[i] = Nibbles.ToByte(nibbles[2 * i], nibbles[2 * i + 1]);
        }

        if (nibbles.Length % 2 == 1)
        {
            bytes[nibbles.Length / 2] = Nibbles.ToByte(nibbles[^1], 0);
        }
    }

    public readonly TreePath Append(Span<byte> nibbles)
    {
        if (nibbles.Length == 0) return this;
        if (nibbles.Length == 1) return Append(nibbles[0]);

        TreePath copy = this;
        copy.AppendMut(nibbles);
        return copy;
    }

    public readonly TreePath Append(byte nib)
    {
        TreePath copy = this;
        copy.AppendMut(nib);
        return copy;
    }

    internal void AppendMut(Span<byte> nibbles)
    {
        if (nibbles.Length == 0) return;
        if (nibbles.Length == 1)
        {
            AppendMut(nibbles[0]);
            return;
        }

        Span<byte> pathSpan = Span;

        if (Length % 2 == 1)
        {
            this[Length] = nibbles[0];
            Length++;
            nibbles = nibbles[1..];
        }

        int byteLength = nibbles.Length / 2;
        int pathSpanStart = Length / 2;
        for (int i = 0; i < byteLength; i++)
        {
            pathSpan[i + pathSpanStart] = Nibbles.ToByte(nibbles[i * 2], nibbles[i * 2 + 1]);
            Length += 2;
        }

        if (nibbles.Length % 2 == 1)
        {
            this[Length] = nibbles[^1];
            Length++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AppendMut(byte nib)
    {
        this[Length] = nib;
        Length++;
    }

    public readonly byte this[int childPathLength]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (childPathLength >= 64) throw new IndexOutOfRangeException();
            if (childPathLength % 2 == 0)
            {
                return (byte)((Span[childPathLength / 2] & 0xf0) >> 4);
            }
            else
            {
                return (byte)(Span[childPathLength / 2] & 0x0f);
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            if (childPathLength >= 64) throw new IndexOutOfRangeException();
            if (childPathLength % 2 == 0)
            {
                Span[childPathLength / 2] =
                    (byte)((Span[childPathLength / 2] & 0x0f) |
                           (value & 0x0f) << 4);
            }
            else
            {
                Span[childPathLength / 2] =
                    (byte)((Span[childPathLength / 2] & 0xf0) |
                           ((value & 0x0f)));
            }
        }
    }

    public readonly TreePath Truncate(int pathLength)
    {
        TreePath copy = this;
        copy.TruncateMut(pathLength);
        return copy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TruncateMut(int pathLength)
    {
        if (pathLength > Length) throw new IndexOutOfRangeException("path length must be less than current length");
        if (pathLength == Length) return;

        if (Length % 2 == 1)
        {
            this[Length - 1] = 0;
            Length--;
        }

        if (pathLength == Length) return;

        int byteClearStart = (pathLength + 1) / 2;
        Span[byteClearStart..(Length / 2)].Clear();

        if (pathLength % 2 == 1)
        {
            this[pathLength] = 0;
        }

        Length = pathLength;
    }

    public override readonly string ToString()
    {
        return $"{Length} {Path}";
    }

    public static bool operator ==(in TreePath left, in TreePath right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(in TreePath left, in TreePath right)
    {
        return !(left == right);
    }

    public readonly bool Equals(in TreePath other)
    {
        return Path.Equals(other.Path) && Length == other.Length;
    }

    public readonly override bool Equals(object? obj)
    {
        return obj is TreePath other && Equals(other);
    }

    public readonly override int GetHashCode()
    {
        return HashCode.Combine(Path, Length);
    }

    /// <summary>
    /// Used for scoped pattern where inside the scope the path is appended with some nibbles and it will
    /// truncate back to previous length on dispose. Cut down on memory allocations.
    /// </summary>
    public ref struct AppendScope
    {
        private readonly int _previousLength;
        private ref TreePath _path;

        public AppendScope(int previousLength, ref TreePath path)
        {
            _previousLength = previousLength;
            _path = ref path;
        }

        public void Dispose()
        {
            _path.TruncateMut(_previousLength);
        }
    }
}

public static class TreePathExtensions
{
    public static TreePath.AppendScope ScopedAppend(this ref TreePath path, Span<byte> nibbles)
    {
        int previousLength = path.Length;
        path.AppendMut(nibbles);
        return new TreePath.AppendScope(previousLength, ref path);
    }

    public static TreePath.AppendScope ScopedAppend(this ref TreePath path, byte nibble)
    {
        int previousLength = path.Length;
        path.AppendMut(nibble);
        return new TreePath.AppendScope(previousLength, ref path);
    }
}
