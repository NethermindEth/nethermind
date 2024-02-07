// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie;

/// <summary>
/// Patricia trie tree path. Can represent up to 64 nibbles in 32+4 byte.
/// Can be used as ref struct, and mutated during trie traversal.
/// </summary>
[Todo("check if its worth it to change the length to byte, or if it actually make things slower.")]
[Todo("check if its worth it to not clear byte during TruncateMut, but will need proper comparator, span copy, etc.")]
public struct TreePath
{
    public const int MemorySize = 36;
    public readonly ValueHash256 Path;

    public static TreePath Empty => new TreePath();

    public readonly Span<byte> Span => Path.BytesAsSpan;

    public TreePath(in ValueHash256 path, int length)
    {
        if (length > 64) throw new InvalidOperationException("TreePath can't represent more than 64 nibble.");
        Path = path;
        Length = length;
    }

    public int Length { get; internal set; }

    public static TreePath FromPath(ReadOnlySpan<byte> pathHash)
    {
        if (pathHash.Length > 32) throw new InvalidOperationException("Path must be at most 32 byte");
        if (pathHash.Length == 32) return new TreePath(new ValueHash256(pathHash), 64);

        // Some of the test passes path directly to PatriciaTrie, but its not 32 byte.
        TreePath newTreePath = new TreePath();
        pathHash.CopyTo(newTreePath.Span);
        newTreePath.Length = pathHash.Length * 2;
        return newTreePath;
    }

    // Mainly used in testing code
    public static TreePath FromHexString(string hexString)
    {
        string toHashHex = hexString;
        if (hexString.Length < 64)
        {
            toHashHex += string.Concat(Enumerable.Repeat('0', 64 - hexString.Length));
        }
        return new TreePath(new ValueHash256(toHashHex), hexString.Length);
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

    public void AppendMut(in TreePath otherTreePath)
    {
        if (Length + otherTreePath.Length > 64)
            throw new InvalidOperationException("Combined nibble length must be less or equal to 64!");

        if (Length % 2 == 0)
        {
            // We are currently even, so can just copy byte by byte.
            int byteToCopy = (otherTreePath.Length + 1) / 2;
            otherTreePath.Path.BytesAsSpan[..byteToCopy].CopyTo(Path.BytesAsSpan[(Length/2)..]);
            Length += otherTreePath.Length;
        }
        else
        {
            // Append one nib first.
            AppendMut(otherTreePath[0]);

            int byteToCopy = otherTreePath.Length / 2;
            otherTreePath.Path.BytesAsSpan[..byteToCopy].CopyTo(Path.BytesAsSpan[(Length/2)..]);
            Bytes.ShiftLeft4(Path.BytesAsSpan[(Length/2)..]);

            if (otherTreePath.Length % 2 == 1)
            {
                // Note: if odd, last byte is skipped, as there might not be enough space before shift4.
                // Eg:
                // 01 2 + 34 56 8 (assuming 4 byte storage instead of 32)
                // 01 23 + 34 56 8 (after add first)
                // 01 23 34 56 + 8 (no space for last)
                // 01 23 45 60 + 8 (after shift4, need to add last manually)
                Length += otherTreePath.Length - 2;
                AppendMut(otherTreePath[^1]);
            }
            else
            {
                // If odd and enough space
                // 01 2 + 34 56 80
                // 01 23 + 34 56 80 (after add first)
                // 01 23 34 56 80   (before shift4)
                // 01 23 45 68 00   (after shift4, everything is fine..)
                Length += otherTreePath.Length - 1;
            }
        }
    }

    public void AppendMut(byte nib)
    {
        this[Length] = nib;
        Length++;
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

    public TreePath this[Range range]
    {
        get
        {
            (int offset, int length) = range.GetOffsetAndLength(Length);
            if (offset == 0 && length == Length) return this;
            if (offset == 0)
            {
                TreePath copied = this;
                copied.TruncateMut(length);
                return copied;
            }

            int toCopyLength = length;

            if (offset % 2 == 0)
            {
                int byteOffset = (offset / 2);
                int toCopyByteLength = (toCopyLength + 1) / 2;
                TreePath newTreePath = new TreePath();
                Path.Bytes[byteOffset..(byteOffset+toCopyByteLength)].CopyTo(newTreePath.Path.BytesAsSpan);
                newTreePath.Length = toCopyLength;
                return newTreePath;
            }
            else
            {
                int byteOffset = (offset / 2);
                int toCopyByteLength = (toCopyLength + 2) / 2;
                TreePath newTreePath = new TreePath();
                Path.Bytes[byteOffset..(byteOffset+toCopyByteLength)].CopyTo(newTreePath.Path.BytesAsSpan);
                Bytes.ShiftLeft4(newTreePath.Path.BytesAsSpan[..toCopyByteLength]);
                newTreePath.Length = toCopyLength;
                return newTreePath;
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

    public int CommonPrefixLength(in TreePath otherTreePath)
    {
        int minOfTwoLength = Math.Min(Length, otherTreePath.Length);
        int bytePrefixLength = minOfTwoLength / 2;
        int byteCommonPrefix = Span[..bytePrefixLength].CommonPrefixLength(otherTreePath.Span[..bytePrefixLength]);
        int commonPrefix = byteCommonPrefix*2;
        if (commonPrefix < minOfTwoLength)
        {
            // check additional one nibble after the common prefix determined at byte level.
            // if the other nibble after this one is also the same, then byte level common prefix should already be
            // incremented.
            if (this[commonPrefix] == otherTreePath[commonPrefix])
            {
                commonPrefix++;
            }
        }

        return commonPrefix;
    }

    public byte[] ToNibble()
    {
        bool odd = Length % 2 == 1;
        Span<byte> theNibbles = stackalloc byte[odd ? Length + 1 : Length];
        Nibbles.BytesToNibbleBytes(Path.Bytes[..((Length + 1) / 2)], theNibbles);
        return (odd ? theNibbles[..Length] : theNibbles).ToArray();
    }

    public string ToHexString()
    {
        string fromPath = Path.Bytes.ToHexString();
        return fromPath[..Length];
    }

    public override string ToString()
    {
        return ToHexString();
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

    public int CompareTo(TreePath treePath)
    {
        // TODO: Implement this properly
        return Bytes.BytesComparer.Compare(ToNibble(), treePath.ToNibble());
    }
}

/// <summary>
/// A class wrapper for TreePath. Used in case you can't use TreePath or it could take more memory to do so.
/// Need heap though, so TreePath with `ref` or `in` is probably faster if it does not need to be stored in TreeNode.
/// Note: Careful to copy before doing any mutation. It might be shared between TreeNode.
/// </summary>
public class BoxedTreePath
{
    public const int MemorySize = MemorySizes.SmallObjectOverhead - MemorySizes.SmallObjectFreeDataSize + TreePath.MemorySize;

    public BoxedTreePath(in TreePath newKey)
    {
        TreePath = newKey;
    }

    public static BoxedTreePath FromNibble(byte[] bytes)
    {
        return new BoxedTreePath(TreePath.FromNibble(bytes));
    }

    public static BoxedTreePath FromHexString(string hexString)
    {
        return new BoxedTreePath(TreePath.FromHexString(hexString));
    }

    public BoxedTreePath()
    {
    }

    // Not a property to prevent copy if `in`... well actually the default get is readonly. Maybe it does not copy?
    // Or this does not work as what I expected in the first place?
    public TreePath TreePath;
    public int Length => TreePath.Length;

    public void AppendMut(byte newByte)
    {
        TreePath.AppendMut(newByte);
    }

    public void AppendMut(BoxedTreePath? otherPath)
    {
        if (otherPath == null) return;
        TreePath.AppendMut(otherPath.TreePath);
    }

    public void TruncateMut(int extensionLength)
    {
        TreePath.TruncateMut(extensionLength);
    }

    public int CommonPrefixLength(in BoxedTreePath otherTreePath)
    {
        return TreePath.CommonPrefixLength(otherTreePath.TreePath);
    }

    public static bool operator ==(BoxedTreePath? left, BoxedTreePath? right)
    {
        return left?.TreePath == right?.TreePath;
    }

    public static bool operator !=(BoxedTreePath? left, BoxedTreePath? right)
    {
        return !(left == right);
    }

    protected bool Equals(BoxedTreePath other)
    {
        return TreePath.Equals(other.TreePath);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj is not BoxedTreePath asBoxedTreePath) return false;
        return Equals(asBoxedTreePath);
    }

    public override int GetHashCode()
    {
        return TreePath.GetHashCode();
    }

    public BoxedTreePath Copy()
    {
        return new BoxedTreePath(TreePath);
    }

    public byte this[int idx]
    {
        get => TreePath[idx];
    }

    public BoxedTreePath this[Range range]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            (int offset, int length) = range.GetOffsetAndLength(Length);
            if (offset == 0 && length == Length) return this;
            return new BoxedTreePath(TreePath[range]);
        }
    }

    public override string ToString()
    {
        return TreePath.ToString();
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
