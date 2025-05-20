// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
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
/// Represents a nibble path in a way that makes it efficient for comparisons.
/// </summary>
/// <remarks>
/// The implementation abuses the fact that leafs at the same level will share the oddity and length so that,
/// if a comparison is made it can be odd aligned optimized one.
/// </remarks>
public readonly ref struct NibblePath
{
    private const int NibblePerByte = 2;
    private const int NibbleShift = 8 / NibblePerByte;
    private const int NibbleMask = 15;

    private const int OddBit = 1;

    private readonly ref byte _span;
    private readonly byte _odd;
    public readonly byte Length;

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

    private bool IsOdd => _odd == OddBit;

    private static NibblePath Empty => default;

    public static NibblePath FromKey(ReadOnlySpan<byte> key, int nibbleFrom = 0)
    {
        var count = key.Length * NibblePerByte;
        return new NibblePath(key, nibbleFrom, count - nibbleFrom);
    }

    public static NibblePath FromCompact(byte[] compact)
    {
        if (compact.Length <= 1)
        {
            if (compact.Length == 0 || compact[0] == 0)
                return default;
        }

        byte oddity = (byte)((compact[0] & OddFlag) >> OddFlagShift);
        return new NibblePath(ref compact[1 - oddity], oddity, (byte)((compact.Length - 1) * 2 + oddity));
    }

    [DebuggerStepThrough]
    private NibblePath(ReadOnlySpan<byte> key, int nibbleFrom, int length)
    {
        _span = ref Unsafe.Add(ref MemoryMarshal.GetReference(key), nibbleFrom / 2);
        _odd = (byte)(nibbleFrom & OddBit);
        Length = (byte)length;
    }

    private NibblePath(ref byte span, byte odd, byte length)
    {
        _span = ref span;
        _odd = odd;
        Length = length;
    }

    /// <summary>
    /// Slices the beginning of the nibble path as <see cref="Span{T}.Slice(int)"/> does.
    /// </summary>
    public NibblePath SliceFrom(int start)
    {
        Debug.Assert(Length - start >= 0, "Path out of boundary");

        if (Length - start == 0)
            return Empty;

        return new(ref Unsafe.Add(ref _span, (_odd + start) / 2),
            (byte)((start & 1) ^ _odd), (byte)(Length - start));
    }

    /// <summary>
    /// Trims the end of the nibble path so that it gets to the specified length.
    /// </summary>
    public NibblePath SliceTo(int length)
    {
        Debug.Assert(length <= Length, "Cannot slice the NibblePath beyond its Length");
        return new NibblePath(ref _span, _odd, (byte)length);
    }

    /// <summary>
    /// Trims the end of the nibble path so that it gets to the specified length.
    /// </summary>
    public NibblePath Slice(int start, int length)
    {
        Debug.Assert(start + length <= Length, "Cannot slice the NibblePath beyond its Length");
        return new(ref Unsafe.Add(ref _span, (_odd + start) / 2),
            (byte)((start & 1) ^ _odd), (byte)length);
    }

    public byte this[int nibble] => GetAt(nibble);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetAt(int nibble) => (byte)((GetRefAt(nibble) >> GetShift(nibble)) & NibbleMask);

    private int GetShift(int nibble) => (1 - ((nibble + _odd) & OddBit)) * NibbleShift;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte GetRefAt(int nibble) => ref Unsafe.Add(ref _span, (nibble + _odd) / 2);

    private static int GetSpanLength(int odd, int length) => (length + 1 + odd) / 2;

    /// <summary>
    /// Gets the raw underlying span behind the path, removing the odd encoding.
    /// </summary>
    private ReadOnlySpan<byte> RawSpan => MemoryMarshal.CreateSpan(ref _span, RawSpanLength);

    private int RawSpanLength => GetSpanLength(_odd, Length);

    public int CommonPrefixLength(in NibblePath other)
    {
        var length = Math.Min(other.Length, Length);
        if (length == 0)
        {
            // special case, empty is different at zero
            return 0;
        }

        if (_odd == other._odd)
        {
            // The most common case in Trie.
            // As paths will start on the same level, the odd will be encoded same way for them.
            // This means that an unrolled version can be used.

            ref var left = ref _span;
            ref var right = ref other._span;

            var position = 0;
            var isOdd = (_odd & OddBit) != 0;
            if (isOdd)
            {
                // This means first byte is not a whole byte
                if ((left & NibbleMask) != (right & NibbleMask))
                {
                    // First nibble differs
                    return 0;
                }

                // Equal so start comparing at next byte
                position = 1;
            }

            // Byte length is half of the nibble length
            var byteLength = length / 2;
            if (!isOdd && ((length & 1) > 0))
            {
                // If not isOdd, but the length is odd, then we need to add one more byte
                byteLength += 1;
            }

            ReadOnlySpan<byte> leftSpan =
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref left, position), byteLength);
            ReadOnlySpan<byte> rightSpan =
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref right, position), byteLength);
            var divergence = leftSpan.CommonPrefixLength(rightSpan);

            position += divergence * 2;
            if (divergence == leftSpan.Length)
            {
                // Remove the extra nibble that made it up to a full byte, if added.
                return Math.Min(length, position);
            }

            // Check which nibble is different
            if ((leftSpan[divergence] & 0xf0) == (rightSpan[divergence] & 0xf0))
            {
                // Are equal, so the next nibble is the one that differs
                return position + 1;
            }

            return position;
        }

        return Fallback(in this, in other, length);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Fallback(in NibblePath @this, in NibblePath other, int length)
        {
            // fallback, the slow path version to make the method work in any case
            int i = 0;
            for (; i < length; i++)
            {
                if (@this.GetAt(i) != other.GetAt(i))
                {
                    return i;
                }
            }

            return length;
        }
    }

    public override string ToString()
    {
        if (Length == 0)
            return "";

        Span<char> path = stackalloc char[Length];
        ref var ch = ref path[0];

        for (int i = _odd; i < Length + _odd; i++)
        {
            var b = Unsafe.Add(ref _span, i / 2);
            var nibble = (b >> ((1 - (i & OddBit)) * NibbleShift)) & NibbleMask;

            ch = Hex[nibble];
            ch = ref Unsafe.Add(ref ch, 1);
        }

        return new string(path);
    }

    private static readonly char[] Hex = "0123456789ABCDEF".ToArray();


    public bool Equals(Key key) => Equals(key.AsPath());

    public bool Equals(in NibblePath other)
    {
        if (other.Length != Length)
            return false;

        // Same length

        ref var left = ref _span;
        ref var right = ref other._span;
        var length = Length;

        if (Unsafe.AreSame(ref left, ref right))
        {
            // lengths, oddity and the ref are the same.
            return true;
        }

        if (other._odd == _odd)
        {
            // Oddity is the same

            if (other._odd == OddBit)
            {
                // This means that the first byte represents just one nibble
                if (((left ^ right) & NibbleMask) > 0)
                {
                    // First nibble differs
                    return false;
                }

                // Move beyond first
                left = ref Unsafe.Add(ref left, 1);
                right = ref Unsafe.Add(ref right, 1);

                // One nibble already consumed, reduce the length
                length -= 1;
            }

            if ((length & OddBit) == OddBit)
            {
                const int highNibbleMask = NibbleMask << NibbleShift;

                // Length is odd, which requires checking the last byte but only the first nibble
                if (((Unsafe.Add(ref left, length >> 1) ^ Unsafe.Add(ref right, length >> 1))
                     & highNibbleMask) > 0)
                {
                    return false;
                }

                // Last nibble already consumed, reduce the length
                length -= 1;
            }

            if (length == 0)
                return true;

            Debug.Assert(length % 2 == 0);

            ReadOnlySpan<byte> leftSpan = MemoryMarshal.CreateReadOnlySpan(ref left, length >> 1);
            ReadOnlySpan<byte> rightSpan = MemoryMarshal.CreateReadOnlySpan(ref right, length >> 1);

            return leftSpan.SequenceEqual(rightSpan);
        }

        // Not aligned fallback. Should not occur
        return Fallback(in this, in other);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool Fallback(in NibblePath @this, in NibblePath other)
        {
            // fallback, the slow path version to make the method work in any case
            for (int i = 0; i < @this.Length; i++)
            {
                if (@this.GetAt(i) != other.GetAt(i))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// The <see cref="Key"/> is a materialized <see cref="NibblePath"/> so that it can be kept in <see cref="INodeWithKey.Key"/>.
    /// It uses an encoding similar to Ethereum's compact encoding for the path.
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
    /// - 5 nibbles as 3 bytes
    ///
    /// As shown above for prefix of length 1 and 2, it's not worse than byte-per-nibble encoding,
    /// gaining more from 3 nibbles forward.
    /// </remarks>
    public readonly struct Key : IEquatable<Key>
    {
        private readonly byte[]? _data;

        private Key(byte[] data)
        {
            _data = data;
        }

        public static explicit operator Key(NibblePath path)
        {
            var length = GetRequiredArraySize(path.Length);
            byte[] bytes = GC.AllocateArray<byte>(length);

            if ((path._odd ^ path.Length) != 0)
            {
                // The oddity is not aligned with the length.
                // This will not happen for the leafs as they need to have the rest of the keccak path set.
                // We can handle this case without focusing on performance. Paths will be for extensions and these are usually short.

                if (path.Length % 2 == 1)
                {
                    // Odd lenght, address first the odd one
                    bytes[0] = (byte)(OddFlag | path.GetAt(0));

                    for (int i = 1; i < path.Length; i += 2)
                    {
                        bytes[(i + 1) / 2] = (byte)((path.GetAt(i) << 4) + path.GetAt(i + 1));
                    }
                }
                else
                {
                    bytes[0] = 0;
                    for (int i = 0; i < path.Length; i += 2)
                    {
                        bytes[PreambleLength + i / 2] = (byte)((path.GetAt(i) << 4) + path.GetAt(i + 1));
                    }
                }
            }
            else
            {
                // The oddity and the length are aligned
                if (path.IsOdd)
                {
                    path.RawSpan.CopyTo(bytes);
                    bytes[0] = (byte)((bytes[0] & ZerothMaskForOddPath) | OddFlag);
                }
                else
                {
                    path.RawSpan.CopyTo(bytes.AsSpan(1));
                    bytes[0] = 0;
                }
            }

            return new Key(bytes);
        }

        public NibblePath AsPath()
        {
            var odd = Odd;
            var length = (byte)((_data.Length - PreambleLength) * 2 + odd);

            return length == 0 ? default : new NibblePath(ref _data[1 - odd], odd, length);
        }

        public int MemorySize => _data is null || ReferenceEquals(_data, EmptyBytes)
            ? 0
            : (int)MemorySizes.Align(_data.Length + MemorySizes.ArrayOverhead);

        /// <summary>
        /// The number of bytes needed to encode the nibble path.
        /// </summary>
        public int ByteLength => _data?.Length ?? 0;

        public int Length => (_data!.Length - PreambleLength) * NibblesPerByte + ((_data[0] & OddFlag) >> OddFlagShift);

        public bool IsNull => _data is null;
        public bool IsNullOrEmpty => _data is null || ReferenceEquals(_data, EmptyBytes);

        private const int PreambleLength = 1;
        private const int NibblesPerByte = 2;
        private const int NibbleShift = 8 / NibblesPerByte;
        private const int NibbleMask = 15;

        /// <summary>
        /// A set of single nibble Hex Prefixes.
        /// </summary>
        private static readonly Key[] Singles =
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

        public static Key Single(int nibble) => Singles[nibble];

        public Key Concat(Key other)
        {
            byte[] data;

            int dLength = _data.Length;
            int otherLength = other._data.Length;

            if (other.IsOdd == false)
            {
                // even, a simple case of appending one to another
                data = new byte[_data!.Length + otherLength - PreambleLength];

                // Copy other first, so that the first byte is overwritten underneath
                other._data!.CopyTo(data, dLength - PreambleLength);
                _data.CopyTo(data, 0);
                return new Key(data);
            }

            Debug.Assert(other.IsOdd, "The other is odd");

            int shift = IsOdd ? 0 : 1;

            // In both cases: even+odd and odd+odd the following will be used
            data = new byte[dLength + otherLength - shift];

            // Copy other first, so that the first byte is overwritten underneath
            other._data!.CopyTo(data, dLength - shift);

            // Mix in the last one
            ref byte last = ref data[dLength - shift];
            last = (byte)((last & NibbleMask) | ((_data[^1] & NibbleMask) << NibbleShift));

            // The last one is take care of. It's an even number of nibbles to move. Move byte by byte
            if (IsOdd == false)
            {
                // even & odd, the first byte should be set to odd
                data[0] = (byte)(OddFlag | ((_data[1] >> NibbleShift) & NibbleMask));

                int length = Length / 2 - 1;
                for (int i = 0; i < length; i++)
                {
                    data[i + 1] = (byte)(((_data[i + 1] & NibbleMask) << NibbleShift) |
                                         ((_data[i + 2] >> NibbleShift) & NibbleMask));
                }

                // even & odd, the first byte should be set to odd
                data[0] = (byte)(OddFlag | ((_data[1] >> NibbleShift) & NibbleMask));
            }
            else
            {
                int length = Length / 2;
                for (int i = 0; i < length; i++)
                {
                    data[i + 1] = (byte)(((_data[i] & NibbleMask) << NibbleShift) |
                                         ((_data[i + 1] >> NibbleShift) & NibbleMask));
                }
            }

            return new Key(data);
        }

        public Key PrependWith(byte nibble)
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

            return new Key(bytes);
        }

        private bool IsOdd => (_data[0] & OddFlag) == OddFlag;
        private byte Odd => (byte)((_data[0] & OddFlag) >> OddFlagShift);

        /// <remarks>
        /// The slice will be used mostly by the <see cref="NodeType.Extension"/> and usually should be quite short.
        /// </remarks>
        public Key Slice(int from, int length)
        {
            if (length == 1)
            {
                return Single(this[from]);
            }

            int size = GetRequiredArraySize(length);
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

            return new Key(data);
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
                byte v = this[i];
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
                int h = 1 - ((index & 1) ^ odd);
                return (byte)((b >> (h * NibbleShift)) & NibbleMask);
            }
        }

        public Hash256 AsHash()
        {
            Debug.Assert((_data![0] & OddFlag) == 0);
            return new Hash256(_data.AsSpan(PreambleLength));
        }

        public bool Equals(Key other)
        {
            return other._data.AsSpan().SequenceEqual(_data.AsSpan());
        }

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

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

        private static readonly byte[] EmptyBytes = [0];
        public static readonly Key Empty = new(EmptyBytes);

        public static Key FromNibbles(ReadOnlySpan<byte> nibbles)
        {
            if (nibbles.Length == 0)
                return Empty;

            if (nibbles.Length == 1)
                return Singles[nibbles[0]];

            byte[] bytes = new byte[GetRequiredArraySize(nibbles.Length)];

            ref byte dest = ref bytes[0];
            ref byte source = ref MemoryMarshal.GetReference(nibbles);

            FromNibblesImpl(ref source, ref dest, nibbles.Length);

            return new Key(bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FromNibblesImpl(ref byte source, ref byte dest, int length)
        {
            if (length % 2 == 1)
            {
                dest = (byte)(OddFlag | source);
                source = ref Unsafe.Add(ref source, 1);
                length--;
            }

            // Always move, even if it's even or odd
            dest = ref Unsafe.Add(ref dest, 1);

            Debug.Assert(length % 2 == 0);

            if (Vector128.IsHardwareAccelerated)
            {
                const int chunk = 16;

                if (length < chunk)
                {
                    // Handle cases where there is fewer nibbles than the vector with a few iffs.
                    if (length % 4 == 2)
                    {
                        dest = (byte)((source << NibbleShift) | Unsafe.Add(ref source, 1));
                        dest = ref Unsafe.Add(ref dest, 1);
                        source = ref Unsafe.Add(ref source, 2);
                        length -= 2;
                    }

                    if (length % 8 == 4)
                    {
                        dest = (byte)((source << NibbleShift) | Unsafe.Add(ref source, 1));
                        Unsafe.Add(ref dest, 1) =
                            (byte)((Unsafe.Add(ref source, 2) << NibbleShift) | Unsafe.Add(ref source, 3));
                        dest = ref Unsafe.Add(ref dest, 2);
                        source = ref Unsafe.Add(ref source, 4);
                        length -= 4;
                    }

                    if (length % chunk != 0)
                    {
                        dest = (byte)((source << NibbleShift) | Unsafe.Add(ref source, 1));
                        Unsafe.Add(ref dest, 1) =
                            (byte)((Unsafe.Add(ref source, 2) << NibbleShift) | Unsafe.Add(ref source, 3));
                        Unsafe.Add(ref dest, 2) =
                            (byte)((Unsafe.Add(ref source, 4) << NibbleShift) | Unsafe.Add(ref source, 5));
                        Unsafe.Add(ref dest, 3) =
                            (byte)((Unsafe.Add(ref source, 6) << NibbleShift) | Unsafe.Add(ref source, 7));
                        dest = ref Unsafe.Add(ref dest, 4);
                        source = ref Unsafe.Add(ref source, 8);
                        length -= 8;
                    }

                    Debug.Assert(length == 0);
                }
                else
                {
                    // There's more than a chunk nibbles.
                    // Align the vector by running it through the loop but handling the first run with alignment.

                    // Prepare the “multiply by 16” vector for <<4
                    Vector128<byte> vMul16 = Vector128.Create((byte)16);

                    for (int i = 0; i < length; i += chunk)
                    {
                        // load 16 bytes
                        Vector128<byte> vec = Vector128.LoadUnsafe(in source, (UIntPtr)i);

                        // shift each 4-bit value into the high nibble (<<4)
                        Vector128<byte> highNibbles = Vector128.Multiply(vec, vMul16);

                        // shuffle high and low nibbles into interleaved positions
                        Vector128<byte> hiShuf = Vector128.Shuffle(highNibbles, HighMask);
                        Vector128<byte> loShuf = Vector128.Shuffle(vec, LowMask);

                        // combine them
                        Vector128<byte> packed = Vector128.BitwiseOr(hiShuf, loShuf);

                        // store the low 8 bytes of 'packed' into the destination
                        packed.GetLower().StoreUnsafe(ref Unsafe.Add(ref dest, i / 2));

                        if (i == 0 && length % chunk != 0)
                        {
                            // Ensure alignment
                            int shift = length & (chunk - 1);
                            i -= chunk - shift;
                        }
                    }
                }
            }
            else
            {
                // first align to length of 8
                if (length % 4 == 2)
                {
                    dest = (byte)((source << NibbleShift) | Unsafe.Add(ref source, 1));
                    dest = ref Unsafe.Add(ref dest, 1);
                    source = ref Unsafe.Add(ref source, 2);
                    length -= 2;
                }

                if (length % 8 == 4)
                {
                    dest = (byte)((source << NibbleShift) | Unsafe.Add(ref source, 1));
                    Unsafe.Add(ref dest, 1) =
                        (byte)((Unsafe.Add(ref source, 2) << NibbleShift) | Unsafe.Add(ref source, 3));
                    dest = ref Unsafe.Add(ref dest, 2);
                    source = ref Unsafe.Add(ref source, 4);
                    length -= 4;
                }

                Debug.Assert(length % 8 == 0);

                // unroll by 8
                const int chunk = 8;
                for (int i = 0; i < length; i += chunk)
                {
                    dest = (byte)((source << NibbleShift) | Unsafe.Add(ref source, 1));
                    Unsafe.Add(ref dest, 1) =
                        (byte)((Unsafe.Add(ref source, 2) << NibbleShift) | Unsafe.Add(ref source, 3));
                    Unsafe.Add(ref dest, 2) =
                        (byte)((Unsafe.Add(ref source, 4) << NibbleShift) | Unsafe.Add(ref source, 5));
                    Unsafe.Add(ref dest, 3) =
                        (byte)((Unsafe.Add(ref source, 6) << NibbleShift) | Unsafe.Add(ref source, 7));
                    dest = ref Unsafe.Add(ref dest, chunk / 2);
                    source = ref Unsafe.Add(ref source, chunk);
                }
            }
        }

        private static int GetRequiredArraySize(int nibbleCount) => nibbleCount / 2 + PreambleLength;

        public static Key FromRaw(ReadOnlySpan<byte> bytes)
        {
            byte[] data = new byte[bytes.Length + PreambleLength];
            bytes.CopyTo(data.AsSpan(1));
            return new Key(data);
        }

        /// <summary>
        /// Parses the Ethereum encoded data into a pair of <see cref="Key"/> and <see cref="bool"/>.
        /// </summary>
        public static (Key key, bool isLeaf) FromRlpBytes(ReadOnlySpan<byte> bytes)
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

            return (new Key(path), isLeaf);
        }

        public void EncodeTo(Span<byte> destination, bool isLeaf)
        {
            Debug.Assert(_data != null);
            _data.CopyTo(destination);
            destination[0] = (byte)((isLeaf ? LeafFlag : 0) | destination[0]);
        }

        public static Key FromHexString(string hex)
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

            return isSuccess ? new Key(data) : throw new FormatException("Incorrect hex string");
        }

        public override string ToString() => ToHexString();


        // The only path where it's executed it's an extension check. Extensions are not that long.
        public int CommonPrefixLength(in NibblePath other) => AsPath().CommonPrefixLength(other);

        // PSHUFB mask to pick bytes [0,2,4,…,14] then zero the rest
        private static readonly Vector128<byte> HighMask = Vector128.Create(
            0, 2, 4, 6, 8, 10, 12, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80
        );

        // PSHUFB mask to pick bytes [1,3,5,…,15] then zero
        private static readonly Vector128<byte> LowMask = Vector128.Create(
            1, 3, 5, 7, 9, 11, 13, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80
        );

        public void WriteNibblesTo(Span<byte> destination)
        {
            for (int i = 0; i < Length; i++)
            {
                destination[i] = this[i];
            }
        }
    }
}
