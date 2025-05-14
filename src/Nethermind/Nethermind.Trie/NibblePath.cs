// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

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

    public int Length => (_data!.Length - PreambleLength) * NibblesPerByte + ((_data[0] & OddFlag) >> OddFlagShift);

    public bool IsNull => _data is null;

    /// <summary>
    /// The odd flag of the Ethereum encoding, used for oddity of in memory representation as well.
    /// </summary>
    private const byte OddFlag = 0x10;

    private const byte OddFlagShift = 4;

    /// <summary>
    /// The leaf flag of the Ethereum encoding.
    /// </summary>
    private const byte LeafFlag = 0x20;

    private const byte ZerothMaskForOddPath = 0x0F;

    private const int PreambleLength = 1;
    private const int NibblesPerByte = 2;
    private const int NibbleShift = 8 / NibblesPerByte;
    private const int NibbleMask = 15;

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
        byte[] data;

        var dLength = _data.Length;
        var otherLength = other._data.Length;

        if (other.IsOdd == false)
        {
            // even, a simple case of appending one to another
            data = new byte[_data!.Length + otherLength - PreambleLength];

            // Copy other first, so that the first byte is overwritten underneath
            other._data!.CopyTo(data, dLength - PreambleLength);
            _data.CopyTo(data, 0);
            return new NibblePath(data);
        }

        Debug.Assert(other.IsOdd, "The other is odd");

        var shift = IsOdd ? 0 : 1;

        // In both cases: even+odd and odd+odd the following will be used
        data = new byte[dLength + otherLength - shift];

        // Copy other first, so that the first byte is overwritten underneath
        other._data!.CopyTo(data, dLength - shift);

        // Mix in the last one
        ref var last = ref data[dLength - shift];
        last = (byte)((last & NibbleMask) | ((_data[^1] & NibbleMask) << NibbleShift));

        // The last one is take care of. It's an even number of nibbles to move. Move byte by byte
        if (IsOdd == false)
        {
            // even & odd, the first byte should be set to odd
            data[0] = (byte)(OddFlag | ((_data[1] >> NibbleShift) & NibbleMask));

            var length = Length / 2 - 1;
            for (var i = 0; i < length ; i++)
            {
                data[i + 1] = (byte)(((_data[i + 1] & NibbleMask) << NibbleShift) |
                                     ((_data[i + 2] >> NibbleShift) & NibbleMask));
            }

            // even & odd, the first byte should be set to odd
            data[0] = (byte)(OddFlag | ((_data[1] >> NibbleShift) & NibbleMask));
        }
        else
        {
            var length = Length / 2;
            for (var i = 0; i < length; i++)
            {
                data[i + 1] = (byte)(((_data[i] & NibbleMask) << NibbleShift) |
                                     ((_data[i + 1] >> NibbleShift) & NibbleMask));
            }
        }

        return new NibblePath(data);
    }

    public NibblePath PrependWith(byte nibble)
    {
        byte[] bytes;

        if (IsOdd)
        {
            // odd
            bytes = new byte[_data!.Length + 1];
            _data.CopyTo(bytes, 1);
            bytes[1] = (byte)((_data[0] & ~OddFlag) | // remove oddity
                              (nibble << NibbleShift)); // or with nibble
        }
        else
        {
            // even, squeeze in
            bytes = new byte[_data!.Length];
            _data.CopyTo(bytes, 0);
            bytes[0] = (byte)(OddFlag | nibble);
        }

        return new NibblePath(bytes);
    }

    private bool IsOdd => (_data[0] & OddFlag) == OddFlag;

    public NibblePath SliceTo(int end) => Slice(0, end);

    public NibblePath SliceFrom(int start) => Slice(start, Length - start);

    public NibblePath Slice(int from, int length)
    {
        throw new NotImplementedException();
    }

    public string ToHexString()
    {
        const int prefixLength = 2;

        // TODO: optimize
        Span<char> chars = stackalloc char[Length + prefixLength];

        chars[0] = '0';
        chars[1] = 'x';

        for (int i = 0; i < Length; i++)
        {
            var v = this[i];
            chars[i + prefixLength] = (char)(v < 10 ? '0' + v : 'a' + v - 10);
        }

        return new string(chars);
    }

    public byte this[int index]
    {
        get
        {
            int odd = (_data[0] & OddFlag) >> OddFlagShift;
            byte b = _data[(index + 2 - odd) / 2];

            // byte is two nibbles
            // for an odd path, and an odd index, take higher nibble
            // for an odd path, and an even index, take lower nibble
            // for an even path, and an even index, take higher nibble
            // for an even path, and an odd index, take lower nibble
            var h = 1 - ((index & 1) ^ odd);
            return (byte)((b >> (h * NibbleShift)) & NibbleMask);
        }
    }

    public Hash256 AsHash()
    {
        Debug.Assert((_data![0] & OddFlag) == 0);
        return new Hash256(_data.AsSpan(PreambleLength));
    }

    public bool Equals(NibblePath other)
    {
        return other._data.AsSpan().SequenceEqual(_data.AsSpan());
    }

    public static readonly NibblePath Empty = new([0]);

    public static NibblePath FromNibbles(ReadOnlySpan<byte> nibbles)
    {
        var bytes = new byte[GetRequiredArraySize(nibbles.Length)];

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

    private static int GetRequiredArraySize(int nibbleCount) => nibbleCount / 2 + PreambleLength;

    public static NibblePath FromRaw(ReadOnlySpan<byte> bytes)
    {
        byte[] data = new byte[bytes.Length + PreambleLength];
        bytes.CopyTo(data.AsSpan(1));
        return new NibblePath(data);
    }

    /// <summary>
    /// Parses the Ethereum encoded data into a pair of <see cref="NibblePath"/> and <see cref="bool"/>.
    /// </summary>
    public static (NibblePath key, bool isLeaf) FromRlpBytes(ReadOnlySpan<byte> bytes)
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
        destination[0] = (byte)((isLeaf ? LeafFlag : 0) | destination[0]);
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


        public static Ref Empty => default;

        public Ref(in ReadOnlySpan<byte> rawKey) : this(rawKey, 0, rawKey.Length * 2)
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

    public static NibblePath FromHexString(string hex)
    {
        throw new NotImplementedException();
        // Bytes.FromHexString(hex)
    }
}
