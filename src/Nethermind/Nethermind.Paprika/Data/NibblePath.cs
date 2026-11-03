using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Crypto;

namespace Paprika.Data;

/// <summary>
/// Represents a nibble path in a way that makes it efficient for comparisons.
/// </summary>
/// <remarks>
/// The implementation diverges from the Ethereum encoding for extensions or leafs.
/// The divergence is to never perform bit shift of the whole path and always align to byte boundary.
/// If the path starts in on odd nibble, it will include one byte and use only its higher nibble.
/// </remarks>
public readonly ref struct NibblePath
{
    /// <summary>
    /// An array of singles, that can be used to create a path of length 1, both odd and even.
    /// Used by <see cref="Single"/>.
    /// </summary>
    private static ReadOnlySpan<byte> Singles => new byte[]
    {
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF
    };

    private static ReadOnlySpan<byte> Doubles => new byte[]
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
        0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
        0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F,
        0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
        0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
        0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
        0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
        0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
        0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
        0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF,
    };

    /// <summary>
    /// Creates a <see cref="NibblePath"/> with length of 1.
    /// </summary>
    /// <param name="nibble">The nibble that should be in the path.</param>
    /// <param name="odd">The oddity.</param>
    /// <returns>The path</returns>
    /// <remarks>
    /// Highly optimized, branchless, just a few moves and adds.
    /// </remarks>
    public static NibblePath Single(byte nibble, int odd)
    {
        Debug.Assert(nibble <= NibbleMask, "Nibble breached the value");
        Debug.Assert(odd <= 1, "Odd should be 1 or 0");

        ref var singles = ref Unsafe.AsRef(ref MemoryMarshal.GetReference(Singles));
        return new NibblePath(ref Unsafe.Add(ref singles, nibble), (byte)odd, 1);
    }

    /// <summary>
    /// Creates an even <see cref="NibblePath"/> with length of 2.
    /// </summary>
    /// <returns>The path</returns>
    /// <remarks>
    /// Highly optimized, branchless, just a few moves and adds.
    /// </remarks>
    public static NibblePath DoubleEven(byte nibble0, byte nibble1) =>
        DoubleEven((byte)((nibble0 << NibbleShift) + nibble1));

    /// <summary>
    /// Creates an even <see cref="NibblePath"/> with length of 2.
    /// </summary>
    /// <returns>The path</returns>
    /// <remarks>
    /// Highly optimized, branchless, just a few moves and adds.
    /// </remarks>
    public static NibblePath DoubleEven(byte combined)
    {
        ref var doubles = ref Unsafe.AsRef(ref MemoryMarshal.GetReference(Doubles));
        return new NibblePath(ref Unsafe.Add(ref doubles, combined), 0, 2);
    }

    public const int MaxLengthValue = byte.MaxValue / 2 + 2;
    public const int NibblePerByte = 2;
    public const int NibbleShift = 8 / NibblePerByte;
    public const int NibbleMask = 15;

    private const int LengthShift = 1;
    private const int PreambleLength = 1;
    private const int OddBit = 1;

    private readonly ref byte _span;

    private readonly int _lengthAndOdd;

    public bool IsOdd => Odd != 0;
    public int Length => _lengthAndOdd >> LengthShift;
    public int Odd => _lengthAndOdd & OddBit;

    public static NibblePath Empty => default;

    public bool IsEmpty => _lengthAndOdd == 0;

    public static NibblePath Parse(string hex)
    {
        var nibbles = new byte[(hex.Length + 1) / 2];
        var path = FromKey(nibbles).SliceTo(hex.Length);

        for (var i = 0; i < hex.Length; i++)
        {
            path.UnsafeSetAt(i, byte.Parse(hex.AsSpan(i, 1), NumberStyles.HexNumber));
        }

        return path;
    }

    public static NibblePath FromKey(ReadOnlySpan<byte> key, int nibbleFrom = 0)
    {
        var count = key.Length * NibblePerByte;
        return new NibblePath(key, nibbleFrom, count - nibbleFrom);
    }

    public static NibblePath FromKey(ReadOnlySpan<byte> key, int nibbleFrom, int length)
    {
        return new NibblePath(key, nibbleFrom, length);
    }

    /// <summary>
    /// Creates a nibble path from raw nibbles (a byte per nibble), using the <paramref name="workingSet"/> as the memory to use.
    /// </summary>
    public static NibblePath FromRawNibbles(ReadOnlySpan<byte> nibbles, Span<byte> workingSet)
    {
        var span = workingSet.Slice(0, (nibbles.Length + 1) / 2);
        var copy = new NibblePath(span, 0, nibbles.Length);

        for (int i = 0; i < nibbles.Length; i++)
        {
            copy.UnsafeSetAt(i, nibbles[i]);
        }

        return copy;
    }

    public ref byte UnsafeSpan => ref _span;

    /// <summary>
    /// Creates the nibble path from preamble and raw slice
    /// </summary>
    public static NibblePath FromRaw(byte preamble, ReadOnlySpan<byte> slice)
    {
        return new NibblePath(slice, preamble & OddBit, preamble >> LengthShift);
    }

    /// <summary>
    /// </summary>
    /// <param name="key"></param>
    /// <param name="nibbleFrom"></param>
    /// <returns>
    /// The Keccak needs to be "in" here, as otherwise a copy would be create and the ref
    /// would point to a garbage memory.
    /// </returns>
    [DebuggerStepThrough]
    public static NibblePath FromKey(in Keccak key, int nibbleFrom = 0)
    {
        var count = Keccak.Size * NibblePerByte;
        return new NibblePath(key.BytesAsSpan, nibbleFrom, count - nibbleFrom);
    }

    /// <summary>
    /// Returns the underlying payload as <see cref="Keccak"/>.
    /// It does it in an unsafe way and requires an external check whether it's possible.
    /// </summary>
    public ref Keccak UnsafeAsKeccak => ref Unsafe.As<byte, Keccak>(ref _span);

    [DebuggerStepThrough]
    private NibblePath(ReadOnlySpan<byte> key, int nibbleFrom, int length)
    {
        _span = ref Unsafe.Add(ref MemoryMarshal.GetReference(key), nibbleFrom / 2);
        _lengthAndOdd = (length << LengthShift) | (nibbleFrom & OddBit);
    }

    private NibblePath(ref byte span, int odd, byte length)
    {
        _span = ref span;
        _lengthAndOdd = (length << LengthShift) | odd;
    }

    /// <summary>
    /// The estimate of the max length, used for stackalloc estimations.
    /// </summary>
    public int MaxByteLength => Length / 2 + 2;

    public const int KeccakNibbleCount = Keccak.Size * NibblePerByte;

    public const int FullKeccakByteLength = Keccak.Size + 2;

    /// <summary>
    /// Writes the nibble path into the destination.
    /// </summary>
    /// <param name="destination">The destination to write to.</param>
    /// <returns>The leftover that other writers can write to.</returns>
    public Span<byte> WriteToWithLeftover(Span<byte> destination)
    {
        var length = WriteImpl(destination);
        return destination.Slice(length);
    }

    /// <summary>
    /// Writes the nibbles to the destination.  
    /// </summary>
    /// <param name="destination"></param>
    /// <returns>The actual bytes written.</returns>
    public Span<byte> WriteTo(Span<byte> destination)
    {
        var length = WriteImpl(destination);
        return destination.Slice(0, length);
    }

    public byte RawPreamble => (byte)_lengthAndOdd;

    private int WriteImpl(Span<byte> destination)
    {
        var odd = Odd;
        var length = Length;
        var spanLength = GetSpanLength(odd, length);

        destination[0] = (byte)_lengthAndOdd;

        ref var destStart = ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), PreambleLength);
        if (spanLength == sizeof(byte))
        {
            destStart = _span;
        }
        else if (spanLength == sizeof(ushort))
        {
            Unsafe.As<byte, ushort>(ref destStart) = Unsafe.As<byte, ushort>(ref _span);
        }
        else if (spanLength == sizeof(ushort) + sizeof(byte))
        {
            Unsafe.As<byte, ushort>(ref destStart) = Unsafe.As<byte, ushort>(ref _span);
            Unsafe.Add(ref destStart, sizeof(ushort)) = Unsafe.Add(ref _span, sizeof(ushort));
        }
        else if (!Unsafe.AreSame(ref _span, ref destStart))
        {
            MemoryMarshal.CreateSpan(ref _span, spanLength).CopyTo(destination.Slice(PreambleLength));
        }

        // If the path length is odd, clear the lower nibble of the last byte.
        // This ensures that truncated nibble paths (like 0xAB.SliceTo(1)) are stored unambiguously.
        // For instance, 0xAB.SliceTo(1) should result in 0xA0, so it can be distinguished from other paths.
        if (((odd + length) & 1) != 0)
        {
            ref var oldest = ref destination[spanLength];
            oldest = (byte)(oldest & 0b1111_0000);
        }

        return spanLength + PreambleLength;
    }

    /// <summary>
    /// Slices the beginning of the nibble path as <see cref="Span{T}.Slice(int)"/> does.
    /// </summary>
    public NibblePath SliceFrom(int start)
    {
        Debug.Assert(Length - start >= 0, "Path out of boundary");

        if (Length - start == 0)
            return Empty;

        var odd = _lengthAndOdd & OddBit;

        return new(ref Unsafe.Add(ref _span, (odd + start) / 2),
            (byte)((start & 1) ^ odd), (byte)(Length - start));
    }

    /// <summary>
    /// Trims the end of the nibble path so that it gets to the specified length.
    /// </summary>
    public NibblePath SliceTo(int length)
    {
        Debug.Assert(length <= Length, "Cannot slice the NibblePath beyond its Length");

        return new NibblePath(ref _span, Odd, (byte)length);
    }

    /// <summary>
    /// Trims the end of the nibble path so that it gets to the specified length.
    /// </summary>
    public NibblePath Slice(int start, int length)
    {
        Debug.Assert(start + length <= Length, "Cannot slice the NibblePath beyond its Length");

        var odd = Odd;

        return new(ref Unsafe.Add(ref _span, (odd + start) / 2),
            (byte)((start & 1) ^ odd), (byte)length);
    }

    public byte this[int nibble] => GetAt(nibble);

    public byte GetAt(int nibble) => (byte)((GetRefAt(nibble) >> GetShift(nibble)) & NibbleMask);

    private int GetShift(int nibble) => (1 - ((nibble + Odd) & OddBit)) * NibbleShift;

    /// <summary>
    /// Sets a <paramref name="value"/> of the nibble at the given <paramref name="nibble"/> location.
    /// This is unsafe. Use only for owned memory. 
    /// </summary>
    public void UnsafeSetAt(int nibble, byte value)
    {
        ref var b = ref GetRefAt(nibble);
        var shift = GetShift(nibble);
        var mask = NibbleMask << shift;

        b = (byte)((b & ~mask) | (value << shift));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref byte GetRefAt(int nibble) => ref Unsafe.Add(ref _span, (nibble + Odd) / 2);

    /// <summary>
    /// Appends a <paramref name="nibble"/> to the end of the path,
    /// using the <paramref name="workingSet"/> as the underlying memory for the new new <see cref="NibblePath"/>.
    /// </summary>
    /// <remarks>
    /// The copy is required as the original path can be based on the readonly memory.
    /// </remarks>
    /// <returns>The newly copied nibble path.</returns>
    public NibblePath AppendNibble(byte nibble, Span<byte> workingSet)
    {
        if (workingSet.Length < MaxByteLength)
        {
            ThrowNotEnoughMemory();
        }

        // TODO: do a ref comparison with Unsafe, if the same, no need to copy!
        WriteTo(workingSet);

        var appended = new NibblePath(ref workingSet[PreambleLength], Odd, (byte)(Length + 1));
        appended.UnsafeSetAt(Length, nibble);
        return appended;
    }

    /// <summary>
    /// Appends the <see cref="other"/> path using the <paramref name="workingSet"/> as the working memory.
    /// </summary>
    public NibblePath Append(scoped in NibblePath other, Span<byte> workingSet)
    {
        if (workingSet.Length <= MaxByteLength)
        {
            ThrowNotEnoughMemory();
        }

        // TODO: do a ref comparison with Unsafe, if the same, no need to copy!
        WriteTo(workingSet);

        var length = Length;
        var odd = Odd;

        var appended = new NibblePath(ref workingSet[PreambleLength], odd, (byte)(length + other.Length));

        // TODO: if aligned, can be based on bytes
        var i = 0;

        var otherOdd = other.Odd;
        if (otherOdd == 0 && ((odd + length) & OddBit) == otherOdd)
        {
            // Aligned, where the first ends, the second starts.
            for (; i < other.Length; i += 2)
            {
                Unsafe.Add(ref appended._span, (odd + length + i) / 2) = Unsafe.Add(ref other._span, i / 2);
            }
        }

        for (; i < other.Length; i++)
        {
            appended.UnsafeSetAt(length + i, other[i]);
        }

        return appended;
    }

    public byte Nibble0 => (byte)((_span >> ((1 - Odd) * NibbleShift)) & NibbleMask);

    private static int GetSpanLength(int odd, int length) => (length + 1 + odd) / 2;

    /// <summary>
    /// Gets the raw underlying span behind the path, removing the odd encoding.
    /// </summary>
    public ReadOnlySpan<byte> RawSpan => MemoryMarshal.CreateSpan(ref _span, RawSpanLength);

    public int RawSpanLength => GetSpanLength(Odd, Length);

    public static ReadOnlySpan<byte> ReadFrom(ReadOnlySpan<byte> source, out NibblePath nibblePath)
    {
        var b = (int)source[0];

        var odd = OddBit & b;
        var length = (b >> LengthShift);

        source = source.Slice(PreambleLength);

        nibblePath = new NibblePath(source, odd, length);
        return source.Slice(GetSpanLength(odd, length));
    }

    public static Span<byte> ReadFrom(Span<byte> source, out NibblePath nibblePath)
    {
        var b = (int)source[0];

        var odd = OddBit & b;
        var length = (b >> LengthShift);

        source = source.Slice(PreambleLength);
        nibblePath = new NibblePath(source, odd, length);
        return source.Slice(GetSpanLength(odd, length));
    }

    public static bool TryReadFrom(scoped in Span<byte> source, in NibblePath expected, out Span<byte> leftover)
    {
        if (source[0] != expected.RawPreamble)
        {
            leftover = default;
            return false;
        }

        leftover = ReadFrom(source, out var actualKey);
        return actualKey.Equals(expected);
    }

    public int FindFirstDifferentNibble(in NibblePath other)
    {
        var length = Math.Min(other.Length, Length);
        if (length == 0)
        {
            // special case, empty is different at zero
            return 0;
        }

        var odd = Odd;

        if (odd == other.Odd)
        {
            // The most common case in Trie.
            // As paths will start on the same level, the odd will be encoded same way for them.
            // This means that an unrolled version can be used.

            ref var left = ref _span;
            ref var right = ref other._span;

            var position = 0;
            var isOdd = odd != 0;
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

            var leftSpan = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref left, position), byteLength);
            var rightSpan = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref right, position), byteLength);
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

    private const int HexPreambleLength = 1;

    public int HexEncodedLength => Length / NibblePerByte + HexPreambleLength;

    private const byte OddFlag = 0x10;
    private const byte LeafFlag = 0x20;

    public void HexEncode(Span<byte> destination, bool isLeaf)
    {
        destination[0] = (byte)(isLeaf ? LeafFlag : 0x000);

        var odd = Odd;

        // This is the usual fast path for leaves, as they are aligned with oddity and length.
        // length: odd, odd: 1
        // length: even, odd: 0
        if ((Length & OddBit) == odd)
        {
            if (odd == OddBit)
            {
                // store odd
                destination[0] += (byte)(OddFlag + (_span & NibbleMask));
            }

            // copy the rest as is
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _span, odd), Length / 2)
                .CopyTo(destination.Slice(HexPreambleLength));

            return;
        }

        // this cases should happen only on extensions, as leafs are aligned to the end of the key.
        ref var b = ref _span;

        // length: even, odd: 1
        if (odd == OddBit)
        {
            // the length is even, no need to amend destination[0]
            for (var i = 0; i < Length / 2; i++)
            {
                destination[i + 1] = (byte)(((b & NibbleMask) << NibbleShift) |
                                            ((Unsafe.Add(ref b, 1) >> NibbleShift) & NibbleMask));
                b = ref Unsafe.Add(ref b, 1);
            }

            return;
        }

        // length: odd, odd: 0
        if ((Length & OddBit) == OddBit)
        {
            destination[0] += (byte)(OddFlag + ((b >> NibbleShift) & NibbleMask));

            // the length is even, no need to amend destination[0]
            for (var i = 0; i < Length / 2; i++)
            {
                destination[i + 1] = (byte)(((b & NibbleMask) << NibbleShift) |
                                            ((Unsafe.Add(ref b, 1) >> NibbleShift) & NibbleMask));
                b = ref Unsafe.Add(ref b, 1);
            }

            return;
        }


        throw new Exception("WRONG!");
    }

    public override string ToString()
    {
        if (Length == 0)
            return "";

        Span<char> path = stackalloc char[Length];
        ref var ch = ref path[0];

        var odd = Odd;

        for (int i = odd; i < Length + odd; i++)
        {
            var b = Unsafe.Add(ref _span, i / 2);
            var nibble = (b >> ((1 - (i & OddBit)) * NibbleShift)) & NibbleMask;

            ch = Hex[nibble];
            ch = ref Unsafe.Add(ref ch, 1);
        }

        return new string(path);
    }

    /// <summary>
    /// For tests
    /// </summary>
    /// <returns></returns>
    public string ToHexByteString()
    {
        if (Length == 0)
            return "";

        Span<char> path = stackalloc char[Length * 2];
        ref var ch = ref path[0];

        var odd = Odd;

        for (int i = odd; i < Length + odd; i++)
        {
            var b = Unsafe.Add(ref _span, i / 2);
            var nibble = (b >> ((1 - (i & OddBit)) * NibbleShift)) & NibbleMask;

            ch = Hex[0];
            ch = ref Unsafe.Add(ref ch, 1);
            ch = Hex[nibble];
            ch = ref Unsafe.Add(ref ch, 1);
        }

        return new string(path).ToLower();
    }

    public void WriteToKeccak(Span<byte> keccak)
    {
        if (IsOdd)
        {
            int ki = 0;
            for (int i = 0; i < Length / 2; i++)
            {
                var highByte = Unsafe.Add(ref _span, i);
                var lowByte = Unsafe.Add(ref _span, i + 1);
                var nibbleH = highByte & 0x0F;
                var nibbleL = (lowByte & 0xF0) >> 4;

                keccak[ki++] = (byte)((nibbleH << 4) | nibbleL);
            }
        }
        else
        {
            RawSpan.CopyTo(keccak);
        }
    }

    private static readonly char[] Hex = "0123456789ABCDEF".ToArray();

    // TODO: optimize
    public bool StartsWith(in NibblePath prefix)
    {
        if (prefix.Length > Length)
            return false;

        return SliceTo(prefix.Length).Equals(prefix);
    }

    public bool Equals(in NibblePath other)
    {
        if (other._lengthAndOdd != _lengthAndOdd)
            return false;

        ref var left = ref _span;
        ref var right = ref other._span;
        var length = Length;

        if (other.Odd != 0)
        {
            // This means first byte is not a whole byte
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

        var leftSpan = MemoryMarshal.CreateReadOnlySpan(ref left, length >> 1);
        var rightSpan = MemoryMarshal.CreateReadOnlySpan(ref right, length >> 1);

        return leftSpan.SequenceEqual(rightSpan);
    }

    public override int GetHashCode()
    {
        if (Length <= 1)
        {
            // for a single nibble path, make it different from empty.
            return Length == 0 ? 0 : 1 << GetAt(0);
        }

        unchecked
        {
            ref var span = ref _span;

            uint hash = (uint)Length << 24;
            nuint length = (nuint)Length;

            if (Odd != 0)
            {
                // mix in first half
                hash |= (uint)(_span & 0x0F) << 20;
                span = ref Unsafe.Add(ref span, 1);
                length -= 1;
            }

            if (length % 2 == 1)
            {
                // mix in
                hash |= (uint)GetAt((int)length - 1) << 16;
                length -= 1;
            }

            Debug.Assert(length % 2 == 0, "Length should be even here");

            length /= 2; // make it byte

            // 8 bytes
            if (length >= sizeof(long))
            {
                nuint offset = 0;
                nuint longLoop = length - sizeof(long);
                if (longLoop != 0)
                {
                    do
                    {
                        hash = BitOperations.Crc32C(hash,
                            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref span, offset)));
                        offset += sizeof(long);
                    } while (longLoop > offset);
                }

                // Do final hash as sizeof(long) from end rather than start
                hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref span, longLoop)));

                return (int)hash;
            }

            // 4 bytes
            if (length >= sizeof(int))
            {
                hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<uint>(ref span));
                length -= sizeof(int);
                if (length > 0)
                {
                    // Do final hash as sizeof(long) from end rather than start
                    hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref span, length)));
                }

                return (int)hash;
            }

            // 2 bytes
            if (length >= sizeof(short))
            {
                hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ushort>(ref span));
                length -= sizeof(short);
                if (length > 0)
                {
                    // Do final hash as sizeof(long) from end rather than start
                    hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref span, length)));
                }

                return (int)hash;
            }

            // 1 byte
            return (int)BitOperations.Crc32C(hash, span);
        }
    }

    public bool HasOnlyZeroes()
    {
        // TODO: optimize
        for (var i = 0; i < Length; i++)
        {
            if (GetAt(i) != 0)
            {
                return false;
            }
        }

        return true;
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowNotEnoughMemory() => throw new ArgumentException("Not enough memory to append");

    public ref struct Builder(Span<byte> workingSet)
    {
        private const int SizeOfId = 4;
        public const int DecentSize = KeccakNibbleCount / NibblePerByte + SizeOfId;

        private readonly NibblePath _path = new(workingSet, 0, workingSet.Length * NibblePerByte);
        private int _length = 0;

        public NibblePath Current => _path.SliceTo(_length);

        public void Push(byte nibble)
        {
            Debug.Assert(nibble < 16);
            Debug.Assert(_length < _path.Length);

            _path.UnsafeSetAt(_length, nibble);
            _length++;
        }

        public void Push(byte nibble0, byte nibble1)
        {
            Debug.Assert(nibble0 < 16);
            Debug.Assert(nibble1 < 16);
            Debug.Assert(_length < _path.Length);

            _path.UnsafeSetAt(_length, nibble0);
            _length++;
            _path.UnsafeSetAt(_length, nibble1);
            _length++;
        }

        public void Pop(int count = 1)
        {
            _length -= count;
            Debug.Assert(_length >= 0);
        }

        /// <summary>
        /// Appends a given path to the accumulated one and returns it for an immediate use.
        /// </summary>
        /// <param name="path">The path to add to</param>
        /// <returns>A concatenated path</returns>
        public NibblePath Append(in NibblePath path)
        {
            for (var i = 0; i < path.Length; i++)
            {
                _path.UnsafeSetAt(_length + i, path.GetAt(i));
            }

            return new NibblePath(ref _path.UnsafeSpan, 0, (byte)(_length + path.Length));
        }

        public void Dispose()
        {
            Debug.Assert(_length == 0);
        }
    }
}
