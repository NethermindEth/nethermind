// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public struct TreePath
{
    public readonly ValueHash256 Path;
    private int _length;

    public static TreePath Empty => new TreePath();

    private readonly Span<byte> Span => Path.BytesAsSpan;

    public TreePath(in ValueHash256 path, int length)
    {
        Path = path;
        _length = length;
    }

    public int Length
    {
        readonly get => _length;
        set => _length = value;
    }

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

    public void AppendMut(Span<byte> nibbles)
    {
        if (nibbles.Length == 0) return;
        if (nibbles.Length == 1)
        {
            AppendMut(nibbles[0]);
            return;
        }

        Span<byte> pathSpan = Span;

        if (_length % 2 == 1)
        {
            this[_length] = nibbles[0];
            _length++;
            nibbles = nibbles[1..];
        }

        int byteLength = nibbles.Length / 2;
        int pathSpanStart = _length / 2;
        for (int i = 0; i < byteLength; i++)
        {
            pathSpan[i + pathSpanStart] = Nibbles.ToByte(nibbles[i * 2], nibbles[i * 2 + 1]);
            _length += 2;
        }

        if (nibbles.Length % 2 == 1)
        {
            this[_length] = nibbles[^1];
            _length++;
        }
    }

    public void AppendMut(byte nib)
    {
        this[_length] = nib;
        _length++;
    }

    public byte this[int childPathLength]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get
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

    public void TruncateMut(int pathLength)
    {
        if (pathLength > _length) throw new IndexOutOfRangeException("path length must be less than current length");
        if (pathLength == _length) return;

        if (_length % 2 == 1)
        {
            this[_length - 1] = 0;
            _length--;
        }

        if (pathLength == _length) return;

        int byteClearStart = (pathLength + 1) / 2;
        Span[byteClearStart..(_length / 2)].Clear();

        if (pathLength % 2 == 1)
        {
            this[pathLength] = 0;
        }

        _length = pathLength;
    }

    public override string ToString()
    {
        return $"{Length} {Path}";
    }
}
