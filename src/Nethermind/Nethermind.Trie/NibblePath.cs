// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

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
            for (var i = 0; i < length; i++)
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
    private byte Odd => (byte)((_data[0] & OddFlag) >> OddFlagShift);

    /// <remarks>
    /// The slice will be used mostly by the <see cref="NodeType.Extension"/> and usually should be quite short.
    /// </remarks>
    public NibblePath Slice(int from, int length)
    {
        if (length == 1)
        {
            return Single(this[from]);
        }

        var size = GetRequiredArraySize(length);
        byte[] data = GC.AllocateUninitializedArray<byte>(size);

        if (length % 2 != 0)
        {
            // odd
            data[0] = (byte)(OddFlag | this[from]);
            from++;
            length--;
        }

        // This part should be really unlikely to happen. Extensions are not long.
        Debug.Assert(length % 2 == 0);

        for (int i = 0; i < length; i += 2)
        {
            data[i / 2 + PreambleLength] = (byte)((this[from + i] << 4) + this[from + i + 1]);
        }

        return new NibblePath(data);
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

    public override bool Equals(object? obj)
    {
        if (obj is NibblePath other)
        {
            return Equals(other);
        }

        return false;
    }

    public override int GetHashCode()
    {
        return _data.Length switch
        {
            0 => 0,
            1 => _data[0],
            2 => Unsafe.ReadUnaligned<ushort>(ref _data[0]),
            3 => Unsafe.ReadUnaligned<ushort>(ref _data[0]),
            _ => Unsafe.ReadUnaligned<int>(ref _data[0]) ^ _data.Length,
        };
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

    public static NibblePath FromHexString(string hex)
    {
        int start = hex is ['0', 'x', ..] ? 2 : 0;
        ReadOnlySpan<char> chars = hex.AsSpan(start);

        if (chars.Length == 0)
        {
            return Empty;
        }

        int oddMod = hex.Length % 2;
        byte[] data = GC.AllocateUninitializedArray<byte>(GetRequiredArraySize(chars.Length));

        bool isSuccess;
        if (oddMod == 0 &&
            BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) &&
            chars.Length >= Vector128<ushort>.Count * 2)
        {
            isSuccess = HexConverter.TryDecodeFromUtf16_Vector128(chars, data.AsSpan(PreambleLength));
        }
        else
        {
            isSuccess = HexConverter.TryDecodeFromUtf16(chars, data.AsSpan(1 - oddMod), oddMod == 1);
            if (oddMod == 1)
            {
                data[0] |= OddFlag;
            }
        }

        return isSuccess ? new NibblePath(data) : throw new FormatException("Incorrect hex string");
    }

    public override string ToString() => ToHexString();

    public int CommonPrefixLength(ReadOnlySpan<byte> remaining)
    {
        var max = Math.Min(Length, remaining.Length);

        for (int i = 0; i < max; i++)
        {
            if (this[i] != remaining[i])
                return i;
        }

        return max;
    }

    public readonly ref struct ByRef
    {
        private readonly ref byte _data;
        private readonly byte _length;

        private ByRef(ref byte data, byte length)
        {
            _length = length;
            _data = ref data;
        }

        public static implicit operator ByRef(NibblePath d)
        {
            return new ByRef(ref MemoryMarshal.GetArrayDataReference(d._data!), (byte)d.Length);
        }

        public int Length => _length;

        public static ByRef FromNibbles(scoped ReadOnlySpan<byte> nibbles, Span<byte> span)
        {
            ref var r = ref MemoryMarshal.GetReference(span);
            int length = (byte)nibbles.Length;

            // nibbles index
            int at = 0;

            // odd
            if (length % 2 == 1)
            {
                r = (byte)(OddFlag | nibbles[at]);

                at++;
                length--;
            }

            // Whether odd or not, move next
            r = ref Unsafe.Add(ref r, 1);

            Debug.Assert(length % 2 == 0);

            // even but not divisible by 4
            if (length % 4 == 2)
            {
                r = (byte)((nibbles[at] << NibbleShift) | nibbles[at + 1]);
                r = ref Unsafe.Add(ref r, 1);

                at += 2;
                length -= 2;
            }

            // even but not divisible by 8
            if (length % 8 == 4)
            {
                r = (byte)((nibbles[at] << NibbleShift) | nibbles[at + 1]);
                Unsafe.Add(ref r, 1) = (byte)((nibbles[at + 2] << NibbleShift) | nibbles[at + 3]);

                r = ref Unsafe.Add(ref r, 2);

                at += 4;
                length -= 4;
            }

            while (length > 0)
            {
                Debug.Assert(length % 8 == 0);

                r = (byte)((nibbles[at] << NibbleShift) | nibbles[at + 1]);
                Unsafe.Add(ref r, 1) = (byte)((nibbles[at + 2] << NibbleShift) | nibbles[at + 3]);
                Unsafe.Add(ref r, 2) = (byte)((nibbles[at + 4] << NibbleShift) | nibbles[at + 5]);
                Unsafe.Add(ref r, 3) = (byte)((nibbles[at + 6] << NibbleShift) | nibbles[at + 7]);

                r = ref Unsafe.Add(ref r, 4);

                at += 8;
                length -= 8;
            }

            return new ByRef(ref MemoryMarshal.GetReference(span), (byte)nibbles.Length);
        }

        public int CommonPrefixLength(scoped in ByRef other)
        {
            var max = Math.Min(Length, other.Length);

            if (max == 0)
                return 0;

            if ((_data & OddFlag) == (other._data & OddFlag))
            {
                // aligned oddity or not
            }

            // slow case of misaligned
            int i = 0;
            for (; i < max; i++)
            {
                if (this[i] != other[i])
                    break;
            }

            return i;
        }

        public NibblePath Slice(int start, int length)
        {
            if (length == 1)
            {
                return Singles[this[start]];
            }

            var size = GetRequiredArraySize(Length);
            var sliceSize = GetRequiredArraySize(length);
            var data = GC.AllocateArray<byte>(sliceSize);

            if (start + length == Length)
            {
                // TODO: more cases can be handed that way, as long as there's the alignments of the oddity of the paths

                // The slice is aligned the same way as this path, ending at the same place
                ReadOnlySpan<byte> toCopy = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref _data, size - sliceSize), sliceSize);
                toCopy.CopyTo(data);

                if (length % 2 == 0)
                {
                    // even, clean the first byte
                    data[0] = 0;
                }
                else
                {
                    // odd, clean the half of it and set the flag
                    data[0] = (byte)((data[0] & 0xF) | OddFlag);
                }
            }
            else
            {
                if (length % 2 != 0)
                {
                    // odd
                    data[0] = (byte)(OddFlag | this[start]);
                    start++;
                    length--;
                }

                // This part should be really unlikely to happen. Extensions are not long.
                Debug.Assert(length % 2 == 0);

                for (int i = 0; i < length; i += 2)
                {
                    data[i / 2 + PreambleLength] = (byte)((this[start + i] << 4) + this[start + i + 1]);
                }
            }

            return new NibblePath(data);
        }

        public byte this [int index]
        {
            get
            {
                ref var d = ref _data;

                int odd = (d & OddFlag) >> OddFlagShift;

                byte b = Unsafe.Add(ref d, (index + 2 - odd) / 2);

                // byte is two nibbles
                // for an odd path, and an odd index, take higher nibble
                // for an odd path, and an even index, take lower nibble
                // for an even path, and an even index, take higher nibble
                // for an even path, and an odd index, take lower nibble
                var h = 1 - ((index & 1) ^ odd);
                return (byte)((b >> (h * NibbleShift)) & NibbleMask);
            }
        }

        public bool Equals(NibblePath other)
        {
            if (other.Length != Length)
                return false;

            return MemoryMarshal.CreateReadOnlySpan(in _data, other.Length)
                .SequenceEqual(other._data);
        }
    }

    public void WriteNibblesTo(Span<byte> destination)
    {
        for (int i = 0; i < Length; i++)
        {
            destination[i] = this[i];
        }
    }
}
