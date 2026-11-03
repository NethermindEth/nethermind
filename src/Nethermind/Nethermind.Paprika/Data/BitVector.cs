using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Dynamic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using static System.Runtime.CompilerServices.Unsafe;

namespace Paprika.Data;

public static class BitVector
{
    public interface IBitVector
    {
        public static abstract ushort Count { get; }

        public bool this[int bit] { get; set; }
    }

    private const int BitsPerByte = 8;
    private const int Shift = 6;
    private const int Mask = (1 << Shift) - 1;

    public const int NotFound = -1;

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    public struct Of1024 : IBitVector
    {
        public const int Size = Count / BitsPerByte;
        public const ushort Count = 1024;

        private byte _start;

        public bool this[int bit]
        {
            readonly get => Get(in _start, bit);
            set => Set(ref _start, bit, value);
        }

        public ushort FirstNotSet => FirstNotSet(this);

        public bool HasEmptyBits => HasEmptyBits(this);

        static ushort IBitVector.Count => Count;
    }

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    public struct Of256 : IBitVector
    {
        public const int Size = Count / BitsPerByte;
        public const ushort Count = 256;

        private byte _start;

        public bool this[int bit]
        {
            readonly get => Get(in _start, bit);
            set => Set(ref _start, bit, value);
        }

        public ushort FirstNotSet => FirstNotSet(this);

        public bool HasEmptyBits => HasEmptyBits(this);

        public bool HasAnySet => Vector256.EqualsAll(Vector256.LoadUnsafe(in _start), Vector256<byte>.Zero) == false;
        public bool HasAllSet => Vector256.EqualsAll(Vector256.LoadUnsafe(in _start), Vector256<byte>.AllBitsSet);

        public int PopCount =>
            BitOperations.PopCount(ReadUnaligned<ulong>(in _start)) +
            BitOperations.PopCount(ReadUnaligned<ulong>(in Add(ref _start, sizeof(ulong)))) +
            BitOperations.PopCount(ReadUnaligned<ulong>(in Add(ref _start, sizeof(ulong) * 2))) +
            BitOperations.PopCount(ReadUnaligned<ulong>(in Add(ref _start, sizeof(ulong) * 3)));

        static ushort IBitVector.Count => Count;

        [Pure]
        public int HighestSmallerOrEqualThan(int maxAt)
        {
            Debug.Assert(maxAt < Count);

            var allowedLane = maxAt >> Shift;
            var allowedBit = maxAt & Mask;

            // Loop from the lane that contains maxAt down to lane 0.
            for (var lane = allowedLane; lane >= 0; lane--)
            {
                const int chunk = sizeof(ulong);
                var bits = ReadUnaligned<ulong>(ref Add(ref _start, chunk * lane));

                // For the lane where maxAt lies, mask out bits above allowedBit.
                if (lane == allowedLane)
                {
                    var mask = allowedBit < 63 ? ((1UL << (allowedBit + 1)) - 1UL) : ~0UL;
                    bits &= mask;
                }

                // If any bit is set in this lane, compute the highest set bit.
                if (bits != 0)
                {
                    var highestInLane = 63 - BitOperations.LeadingZeroCount(bits);
                    return lane * BitsPerByte * chunk + highestInLane;
                }
            }

            return NotFound;
        }

        public Of256 AndNot(in Of256 value)
        {
            var a = Vector256.LoadUnsafe(in _start);
            var b = Vector256.LoadUnsafe(in value._start);

            Of256 result = default;
            Vector256.AndNot(a, b).StoreUnsafe(ref result._start);
            return result;
        }

        public override string ToString() => ToStringImpl(this);
    }

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    public struct Of512 : IBitVector
    {
        public const int Size = Count / BitsPerByte;
        public const ushort Count = 512;

        private byte _start;

        public bool this[int bit]
        {
            readonly get => Get(in _start, bit);
            set => Set(ref _start, bit, value);
        }

        public ushort FirstNotSet => FirstNotSet(this);

        public bool HasEmptyBits => HasEmptyBits(this);

        static ushort IBitVector.Count => Count;
    }

    public static bool HasEmptyBits<TBitVector>(in TBitVector vector)
        where TBitVector : struct, IBitVector
    {
        return FirstNotSet(vector) != TBitVector.Count;
    }

    public static ushort FirstNotSet<TBitVector>(in TBitVector vector)
        where TBitVector : struct, IBitVector
    {
        var size = TBitVector.Count / BitsPerByte;
        const int chunk = sizeof(ulong);
        var count = size / chunk;

        for (var i = 0; i < count; i++)
        {
            var skip = i * chunk;
            ref var b = ref As<TBitVector, byte>(ref AsRef(in vector));

            var v = ReadUnaligned<ulong>(ref Add(ref b, skip));
            if (BitOperations.PopCount(v) != chunk * BitsPerByte)
            {
                return (ushort)(skip * BitsPerByte + BitOperations.TrailingZeroCount(~v));
            }
        }

        return TBitVector.Count;
    }

    public static bool HasAnySet<TBitVector>(in TBitVector vector)
        where TBitVector : struct, IBitVector
    {
        var size = TBitVector.Count / BitsPerByte;
        const int chunk = sizeof(ulong);
        var count = size / chunk;

        for (var i = 0; i < count; i++)
        {
            var skip = i * chunk;
            ref var b = ref As<TBitVector, byte>(ref AsRef(in vector));

            var v = ReadUnaligned<ulong>(ref Add(ref b, skip));
            if (v != 0)
                return true;
        }

        return false;
    }

    private static string ToStringImpl<TBitVector>(in TBitVector vector)
        where TBitVector : struct, IBitVector
    {
        var sb = new StringBuilder(TBitVector.Count + 2);
        sb.Append('{');
        for (var i = 0; i < TBitVector.Count; ++i)
        {
            sb.Append(vector[i] ? "1" : "_");
        }

        sb.Append('}');
        return sb.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Get(in byte b, int bit)
    {
        var at = (uint)bit >> Shift;
        var selector = 1L << (bit & Mask);
        ref var @byte = ref Add(ref As<byte, long>(ref AsRef(in b)), at);
        return (@byte & selector) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Set(ref byte b, int bit, bool value)
    {
        var at = (uint)bit >> Shift;
        var selector = 1L << (bit & Mask);
        ref var @byte = ref Add(ref As<byte, long>(ref b), at);

        if (value)
        {
            @byte |= selector;
        }
        else
        {
            @byte &= ~selector;
        }
    }
}
