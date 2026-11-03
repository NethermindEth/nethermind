using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paprika.Store;

/// <summary>
/// 
/// </summary>
/// <remarks>
/// <see cref="DbAddress"/> stored as 3.5 bytes to get addressable up to 1TB.
///
/// Stored as aa_aa_aa_ba_bb_bb_bb;
/// For little endian:
/// - even index should be read and intersected with <see cref="ValueMask"/>.
/// - odd index should be read and shifted right by <see cref="HalfByteShift"/>.
///         
/// </remarks>
public static class DbAddressList
{
    public static readonly DbAddress Max = DbAddress.Page(ValueMask);

    private const int BytesPer2Addresses = 7;
    private const int Oddity = 1;
    private const uint ValueMask = 0x0F_FF_FF_FF;
    private const byte OneByteMaskLow = 0x0F;
    private const byte OneByteMaskHigh = 0xF0;

    private const int ShiftToOdd = 3;
    private const int HalfByteShift = 4;

    private static DbAddress Get(ref byte b, int index)
    {
        var odd = index & Oddity;
        var i = index >> 1;

        ref var slot = ref Unsafe.Add(ref b, i * BytesPer2Addresses +
                                             odd * ShiftToOdd); // branchless shift for odds

        var value = Unsafe.ReadUnaligned<uint>(ref slot) >> (HalfByteShift * odd); // branchless shift for odds 
        return DbAddress.Page(value & ValueMask);
    }

    private static void Set(ref byte b, int index, DbAddress value)
    {
        var odd = index & Oddity;
        var i = index >> 1;

        ref var slot = ref Unsafe.Add(ref b, i * BytesPer2Addresses);
        uint v;
        if (odd == Oddity)
        {
            // odd
            slot = ref Unsafe.Add(ref slot, ShiftToOdd);

            // Read the previous and set the lowest half-byte to the one from the slot. Shift the rest.
            v = (uint)(slot & OneByteMaskLow) | (value.Raw << HalfByteShift);
        }
        else
        {
            // even
            v = (uint)((Unsafe.Add(ref slot, 3) & OneByteMaskHigh) << 24) | value.Raw;
        }

        Unsafe.WriteUnaligned(ref slot, v);
    }

    public interface IDbAddressList : IClearable
    {
        public DbAddress this[int index] { get; set; }
        public static abstract int Length { get; }

        public bool IsClean { get; }

        public DbAddress[] ToArray();
    }

    public ref struct Enumerator<TList>
        where TList : struct, IDbAddressList
    {
        private readonly ref readonly TList _list;
        private int _index;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(in TList list)
        {
            _list = ref list;
            _index = -1;
        }

        /// <summary>Advances the enumerator to the next element of the span.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            var index = _index + 1;
            if (index < TList.Length)
            {
                _index = index;
                return true;
            }

            return false;
        }

        /// <summary>Gets the element at the current position of the enumerator.</summary>
        public DbAddress Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _list[_index];
        }
    }

    public static Enumerator<Of16> GetEnumerator(this in Of16 list) => new(list);
    public static Enumerator<Of256> GetEnumerator(this in Of256 list) => new(list);
    public static Enumerator<Of1024> GetEnumerator(this in Of1024 list) => new(list);

    private static DbAddress[] ToArrayImpl<TList>(in TList list)
        where TList : struct, IDbAddressList
    {
        var array = new DbAddress[TList.Length];
        for (var i = 0; i < TList.Length; i++)
        {
            array[i] = list[i];
        }

        return array;
    }

    private static bool IsCleanImpl<TList>(in TList list)
        where TList : struct, IDbAddressList
    {
        for (var i = 0; i < TList.Length; i++)
        {
            if (list[i].IsNull == false)
            {
                return false;
            }
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    public struct Of4 : IDbAddressList
    {
        public const int Count = 4;
        public const int Size = DbAddress.Size * Count;

        private DbAddress _b;

        public DbAddress this[int index]
        {
            get
            {
                Debug.Assert(index is >= 0 and < Count);
                return Unsafe.Add(ref _b, index);
            }
            set
            {
                Debug.Assert(index is >= 0 and < Count);
                Unsafe.Add(ref _b, index) = value;
            }
        }

        public static int Length => Count;

        public ReadOnlySpan<DbAddress>.Enumerator GetEnumerator() =>
            MemoryMarshal.CreateReadOnlySpan(ref _b, Count).GetEnumerator();

        public void Clear() => MemoryMarshal.CreateSpan(ref _b, Count).Clear();

        public bool IsClean => IsCleanImpl(this);

        public DbAddress[] ToArray() => ToArrayImpl(this);
    }

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    public struct Of16 : IDbAddressList
    {
        public const int Count = 16;
        public const int Size = Count / 2 * BytesPer2Addresses;

        private byte _b;

        public DbAddress this[int index]
        {
            get
            {
                Debug.Assert(index is >= 0 and < Count);
                return Get(ref _b, index);
            }
            set
            {
                Debug.Assert(index is >= 0 and < Count);
                Set(ref _b, index, value);
            }
        }

        public bool IsClean => IsCleanImpl(this);

        public void Clear() => MemoryMarshal.CreateSpan(ref _b, Size).Clear();

        public static int Length => Count;

        public DbAddress[] ToArray() => ToArrayImpl(this);
    }

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    public struct Of64 : IDbAddressList
    {
        public const int Count = 64;
        public const int Size = Count / 2 * BytesPer2Addresses;

        private byte _b;

        public DbAddress this[int index]
        {
            get
            {
                Debug.Assert(index is >= 0 and < Count);
                return Get(ref _b, index);
            }
            set
            {
                Debug.Assert(index is >= 0 and < Count);
                Set(ref _b, index, value);
            }
        }

        public void Clear() => MemoryMarshal.CreateSpan(ref _b, Size).Clear();

        public bool IsClean => IsCleanImpl(this);

        public static int Length => Count;

        public DbAddress[] ToArray() => ToArrayImpl(this);
    }

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    public struct Of256 : IDbAddressList
    {
        public const int Count = 256;
        public const int Size = Count / 2 * BytesPer2Addresses;

        private byte _b;

        public DbAddress this[int index]
        {
            get
            {
                Debug.Assert(index is >= 0 and < Count);
                return Get(ref _b, index);
            }
            set
            {
                Debug.Assert(index is >= 0 and < Count);
                Set(ref _b, index, value);
            }
        }

        public void Clear() => MemoryMarshal.CreateSpan(ref _b, Size).Clear();

        public bool IsClean => IsCleanImpl(this);

        public static int Length => Count;

        public DbAddress[] ToArray() => ToArrayImpl(this);
    }

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    public struct Of1024 : IDbAddressList
    {
        public const int Count = 1024;
        public const int Size = Count / 2 * BytesPer2Addresses;

        private byte _b;

        public DbAddress this[int index]
        {
            get
            {
                Debug.Assert(index is >= 0 and < Count);
                return Get(ref _b, index);
            }
            set
            {
                Debug.Assert(index is >= 0 and < Count);
                Set(ref _b, index, value);
            }
        }

        public void Clear() => MemoryMarshal.CreateSpan(ref _b, Size).Clear();

        public bool IsClean => IsCleanImpl(this);

        public DbAddress[] ToArray() => ToArrayImpl(this);

        public static int Length => Count;
    }
}
