// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;

namespace Nethermind.Trie;

/// <summary>
/// The <see cref="NibblePath"/> uses almost Ethereum encoding for the path.
/// This ensures that the amount of allocated memory is as small as possible.
/// </summary>
/// <remarks>
/// The 0th byte encodes:
/// - the oddity of the path
/// - 0th nibble if the path is odd.
///
/// This allows encoding the prefix of length:
/// - 1 nibble as 1 byte
/// - 2 nibbles as 2 bytes
/// - 3 nibbles as 2 bytes
/// - 4 nibbles as 3 bytes
///
/// As shown above for prefix of length 1 and 2, it's not worse than byte-per-nibble encoding,
/// gaining more from 3 nibbles forward.
/// </remarks>
public readonly struct NibblePath : IEquatable<NibblePath>
{
    private readonly byte[]? _data;

    private NibblePath(byte[] data)
    {
        _data = data;
    }

    public int MemorySize =>
        _data is not null ? (int)MemorySizes.Align(_data.Length + MemorySizes.ArrayOverhead) : 0;

    /// <summary>
    /// The number of bytes needed to encode the nibble path.
    /// </summary>
    public int ByteLength => _data?.Length ?? 0;

    public int Length =>
        (_data!.Length - PreambleLength) * NibblesPerByte + ((_data[0] & OddFlag) == OddFlag ? 1 : 0);

    public bool IsNull => _data is null;

    /// <summary>
    /// The odd flag of the Ethereum encoding, used for oddity of in memory representation as well.
    /// </summary>
    private const byte OddFlag = 0x10;

    /// <summary>
    /// The leaf flag of the Ethereum encoding.
    /// </summary>
    private const byte LeafFlag = 0x20;

    private const byte ZerothMaskForOddPath = 0x0F;

    private const int PreambleLength = 1;
    private const int NibblesPerByte = 2;

    /// <summary>
    /// A set of single nibble Hex Prefixes.
    /// </summary>
    private static readonly NibblePath[] Singles =
    [
        new([OddFlag | 0]),
        new([OddFlag | 1]),
        new([OddFlag | 2]),
        new([OddFlag | 3]),
        new([OddFlag | 4]),
        new([OddFlag | 5]),
        new([OddFlag | 6]),
        new([OddFlag | 7]),
        new([OddFlag | 8]),
        new([OddFlag | 9]),
        new([OddFlag | 10]),
        new([OddFlag | 11]),
        new([OddFlag | 12]),
        new([OddFlag | 13]),
        new([OddFlag | 14]),
        new([OddFlag | 15])
    ];

    public static NibblePath Single(int nibble) => Singles[nibble];

    public NibblePath Concat(NibblePath other)
    {
        throw new NotImplementedException("NOT IMPLEMENTED YET");
    }

    public NibblePath PrependWith(byte nibble)
    {
        throw new NotImplementedException("NOT IMPLEMENTED YET");
    }

    public NibblePath SliceTo(int end) => throw new NotImplementedException("NOT IMPLEMENTED YET");

    public NibblePath SliceFrom(int start) => throw new NotImplementedException("NOT IMPLEMENTED YET");

    public NibblePath Slice(int from, int length) => throw new NotImplementedException();

    public string ToHexString(bool skipLeadingZeros ) => throw new NotImplementedException("NOT IMPLEMENTED YET");

    public byte this[int index] => throw new NotImplementedException("NOT IMPLEMENTED YET");

    public bool Equals(NibblePath other)
    {
        return other._data.AsSpan().SequenceEqual(_data.AsSpan());
    }

    public static readonly NibblePath Empty = new([0]);

    public static NibblePath FromNibbles(ReadOnlySpan<byte> nibbles)
    {
        var bytes = new byte[nibbles.Length / 2 + PreambleLength ];

        if (nibbles.Length % 2 != 0)
        {
            bytes[0] = (byte)(OddFlag | nibbles[0]);
            nibbles = nibbles[1..];
        }

        for (int i = 0; i < nibbles.Length; i += 2)
        {
            bytes[i / 2 + 1] = (byte)(16 * nibbles[i] + nibbles[i + 1]);
        }

        return new NibblePath(bytes);
    }

    /// <summary>
    /// Parses the Ethereum encoded data into a pair of <see cref="NibblePath"/> and <see cref="bool"/>.
    /// </summary>
    public static (NibblePath key, bool isLeaf) FromBytes(ReadOnlySpan<byte> bytes)
    {
        bool isEven = (bytes[0] & OddFlag) == 0;
        bool isLeaf = bytes[0] >= 32;

        if (!isEven && bytes.Length == 1)
        {
            // Special case of single nibble.
            // Use Single to not allocate.
            return (Singles[bytes[0] & ZerothMaskForOddPath], isLeaf);
        }

        // Use exactly the same length for the prefix as the 0th byte will be overwritten.
        byte[] path = GC.AllocateUninitializedArray<byte>(bytes.Length);

        // Copy as a whole
        bytes.CopyTo(path);

        // Fix the first byte, so that it has only the 0th odd nibble and oddity flag
        path[0] = (byte)(path[0] & (ZerothMaskForOddPath | OddFlag));

        return (new NibblePath(path), isLeaf);
    }

    public void EncodeTo(Span<byte> destination, bool isLeaf)
    {
        Debug.Assert(_data != null);
        _data.CopyTo(destination);
        destination[0] = (byte)(isLeaf ? LeafFlag : 0);
    }

    public int CommonPrefixLength(NibblePath other)
    {
        throw new NotImplementedException();
    }

    public readonly ref struct Ref
    {
        private readonly ref byte _span;
        private readonly byte _odd;
        public readonly byte Length;

        private const int OddBit = 1;
        private const int NibblePerByte = 2;
        private const int NibbleShift = 8 / NibblePerByte;
        private const int NibbleMask = 15;

        public static Ref Empty => default;

        public Ref(in ReadOnlySpan<byte> rawKey) : this (rawKey, 0, rawKey.Length * 2)
        {
        }

        [DebuggerStepThrough]
        private Ref(ReadOnlySpan<byte> key, int nibbleFrom, int length)
        {
            _span = ref Unsafe.Add(ref MemoryMarshal.GetReference(key), nibbleFrom / 2);
            _odd = (byte)(nibbleFrom & OddBit);
            Length = (byte)length;
        }

        private Ref(ref byte span, byte odd, byte length)
        {
            _span = ref span;
            _odd = odd;
            Length = length;
        }

        public byte this[int nibble] => GetAt(nibble);

        public byte GetAt(int nibble) => (byte)((GetRefAt(nibble) >> GetShift(nibble)) & NibbleMask);

        private int GetShift(int nibble) => (1 - ((nibble + _odd) & OddBit)) * NibbleShift;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref byte GetRefAt(int nibble) => ref Unsafe.Add(ref _span, (nibble + _odd) / 2);

        public NibblePath Slice(int start, int length)
        {
            throw new NotImplementedException();
        }

        public Ref Slice(int start)
        {
            Debug.Assert(Length - start >= 0, "Path out of boundary");

            if (Length - start == 0)
                return Empty;

            return new(ref Unsafe.Add(ref _span, (_odd + start) / 2), (byte)((start & 1) ^ _odd),
                (byte)(Length - start));
        }

        public Ref SliceRef(int currentIndex, int remainingUpdatePathLength)
        {
            throw new NotImplementedException();
        }

        public string ToHexString()
        {
            throw new NotImplementedException();
        }

        public NibblePath ToPath()
        {
            throw new NotImplementedException();
        }

        public int CommonPrefixLength(NibblePath nodeKey)
        {
            throw new NotImplementedException();
        }

        public static Ref FromCompact(byte[] bytes)
        {
            throw new NotImplementedException();
        }

        public NibblePath ToNibblePath()
        {
            throw new NotImplementedException();
        }
    }
}
