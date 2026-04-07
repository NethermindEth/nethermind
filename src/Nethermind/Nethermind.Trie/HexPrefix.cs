// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Trie;

public static class HexPrefix
{
    private static readonly byte[][] SingleNibblePaths = CreateSingleNibblePaths();
    private static readonly byte[][] DoubleNibblePaths = CreateDoubleNibblePaths();
    private static readonly byte[][] TripleNibblePaths = CreateTripleNibblePaths();

    public static int ByteLength(byte[] path) => path.Length / 2 + 1;

    public static void CopyToSpan(byte[] path, bool isLeaf, Span<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(ByteLength(path), output.Length, nameof(output));

        output[0] = (byte)(isLeaf ? 0x20 : 0x00);
        if (path.Length % 2 != 0)
        {
            output[0] += (byte)(0x10 + path[0]);
        }

        for (int i = 0; i < path.Length - 1; i += 2)
        {
            output[i / 2 + 1] =
                path.Length % 2 == 0
                    ? (byte)(16 * path[i] + path[i + 1])
                    : (byte)(16 * path[i + 1] + path[i + 2]);
        }
    }

    public static byte[] ToBytes(byte[] path, bool isLeaf)
    {
        byte[] output = new byte[path.Length / 2 + 1];

        CopyToSpan(path, isLeaf, output);

        return output;
    }

    public static (byte[] key, bool isLeaf) FromBytes(ReadOnlySpan<byte> bytes)
    {
        bool isEven = (bytes[0] & 16) == 0;
        bool isLeaf = bytes[0] >= 32;
        int nibblesCount = bytes.Length * 2 - (isEven ? 2 : 1);
        // Return cached arrays for small paths
        switch (nibblesCount)
        {
            case 0:
                return ([], isLeaf);
            case 1:
                // !isEven, bytes.Length == 1
                return (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(SingleNibblePaths), bytes[0] & 0xF), isLeaf);
            case 2:
                // isEven, bytes.Length == 2 - byte value IS the index
                return (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DoubleNibblePaths), bytes[1]), isLeaf);
            case 3:
                // !isEven, bytes.Length == 2
                return (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(TripleNibblePaths), ((bytes[0] & 0xF) << 8) | bytes[1]), isLeaf);
        }

        // Longer paths - allocate
        byte[] path = new byte[nibblesCount];
        Span<byte> span = new(path);
        if (!isEven)
        {
            span[0] = (byte)(bytes[0] & 0xF);
            span = span[1..];
        }
        bytes = bytes[1..];
        Span<ushort> nibbles = MemoryMarshal.CreateSpan(
            ref Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(span)),
            span.Length / 2);
        Debug.Assert(nibbles.Length == bytes.Length);
        ref byte byteRef = ref MemoryMarshal.GetReference(bytes);
        ref ushort lookup16 = ref MemoryMarshal.GetArrayDataReference(Lookup16);
        for (int i = 0; i < nibbles.Length; i++)
        {
            nibbles[i] = Unsafe.Add(ref lookup16, Unsafe.Add(ref byteRef, i));
        }
        return (path, isLeaf);
    }

    private static readonly ushort[] Lookup16 = CreateLookup16();

    private static ushort[] CreateLookup16()
    {
        ushort[] result = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            result[i] = (ushort)(((i & 0xF) << 8) | ((i & 240) >> 4));
        }

        return result;
    }

    /// <summary>
    /// Returns a byte array for the specified nibble path, using cached arrays for short paths (1-3 nibbles) with valid nibble values (0-15) to reduce allocations.
    /// </summary>
    /// <param name="path">The nibble path to convert to a byte array.</param>
    /// <returns>
    /// A byte array representing the nibble path. For paths of length 1-3 with nibble values 0-15, returns a shared cached array that must not be modified.
    /// For longer paths or paths with nibble values >= 16, allocates and returns a new array.
    /// </returns>
    /// <remarks>
    /// This optimization takes advantage of the fact that short nibble paths are common and their possible combinations are limited.
    /// The returned cached arrays are shared and must not be modified by callers.
    /// </remarks>
    public static byte[] GetArray(ReadOnlySpan<byte> path)
    {
        if (path.Length == 0)
        {
            return [];
        }
        if (path.Length == 1)
        {
            uint value = path[0];
            if (value < 16)
            {
                return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(SingleNibblePaths), (int)value);
            }
        }
        else if (path.Length == 2)
        {
            uint v1 = path[1];
            uint v0 = path[0];
            if ((v0 | v1) < 16)
            {
                return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DoubleNibblePaths), (int)((v0 << 4) | v1));
            }
        }
        else if (path.Length == 3)
        {
            uint v2 = path[2];
            uint v1 = path[1];
            uint v0 = path[0];
            if ((v0 | v1 | v2) < 16)
            {
                return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(TripleNibblePaths), (int)((v0 << 8) | (v1 << 4) | v2));
            }
        }
        return path.ToArray();
    }

    /// <summary>
    /// Prepends a nibble to an existing nibble array, returning a cached array for small results.
    /// </summary>
    /// <param name="prefix">The nibble value (0-15) to prepend.</param>
    /// <param name="array">The existing nibble array to prepend to.</param>
    /// <returns>
    /// A cached array if the result length is 3 or fewer nibbles; otherwise a newly allocated array.
    /// </returns>
    public static byte[] PrependNibble(byte prefix, byte[] array)
    {
        switch (array.Length)
        {
            case 0:
                if (prefix < 16)
                {
                    return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(SingleNibblePaths), prefix);
                }
                break;
            case 1:
                {
                    uint v1 = array[0];
                    if ((prefix | v1) < 16)
                    {
                        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DoubleNibblePaths), (prefix << 4) | (int)v1);
                    }
                    break;
                }
            case 2:
                {
                    uint v2 = array[1];
                    uint v1 = array[0];
                    if ((prefix | v1 | v2) < 16)
                    {
                        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(TripleNibblePaths), (prefix << 8) | (int)((v1 << 4) | v2));
                    }
                    break;
                }
        }

        // Fallback - allocate and concat
        byte[] result = new byte[array.Length + 1];
        result[0] = prefix;
        array.CopyTo(result, 1);
        return result;
    }

    /// <summary>
    /// Concatenates two nibble arrays, returning a cached array for small results.
    /// </summary>
    /// <param name="first">The first nibble array.</param>
    /// <param name="second">The second nibble array to append.</param>
    /// <returns>
    /// A cached array if the combined length is 3 or fewer nibbles; otherwise a newly allocated array.
    /// </returns>
    public static byte[] ConcatNibbles(byte[] first, byte[] second)
    {
        switch (first.Length + second.Length)
        {
            case 0:
                return [];
            case 1:
                {
                    byte nibble = first.Length == 1 ? first[0] : second[0];
                    if (nibble < 16)
                    {
                        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(SingleNibblePaths), nibble);
                    }
                    break;
                }
            case 2:
                {
                    (uint v1, uint v2) = first.Length switch
                    {
                        0 => (second[0], second[1]),
                        1 => (first[0], second[0]),
                        _ => (first[0], first[1])
                    };
                    if ((v1 | v2) < 16)
                    {
                        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(DoubleNibblePaths), (int)((v1 << 4) | v2));
                    }
                    break;
                }
            case 3:
                {
                    (uint v1, uint v2, uint v3) = first.Length switch
                    {
                        0 => (second[0], second[1], second[2]),
                        1 => (first[0], second[0], second[1]),
                        2 => (first[0], first[1], second[0]),
                        _ => (first[0], first[1], first[2])
                    };
                    if ((v1 | v2 | v3) < 16)
                    {
                        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(TripleNibblePaths), (int)((v1 << 8) | (v2 << 4) | v3));
                    }
                    break;
                }
        }

        // Fallback - allocate and concat
        byte[] result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    /// <summary>
    /// Returns a cached single-nibble array for a byte value if it's a valid nibble (0-15);
    /// otherwise allocates a new single-element array.
    /// </summary>
    /// <param name="value">The byte value.</param>
    /// <returns>
    /// A cached single-element array if the value is 0-15; otherwise a newly allocated array.
    /// </returns>
    public static byte[] SingleNibble(byte value)
    {
        if (value < 16)
        {
            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(SingleNibblePaths), value);
        }
        return [value];
    }

    private static byte[][] CreateSingleNibblePaths()
    {
        var paths = new byte[16][];
        for (int i = 0; i < 16; i++)
        {
            paths[i] = [(byte)i];
        }
        return paths;
    }

    private static byte[][] CreateDoubleNibblePaths()
    {
        var paths = new byte[256][];
        for (int i = 0; i < 256; i++)
        {
            paths[i] = [(byte)(i >> 4), (byte)(i & 0xF)];
        }
        return paths;
    }

    private static byte[][] CreateTripleNibblePaths()
    {
        var paths = new byte[4096][];
        for (int i = 0; i < 4096; i++)
        {
            paths[i] = [(byte)(i >> 8), (byte)((i >> 4) & 0xF), (byte)(i & 0xF)];
        }
        return paths;
    }
}
