using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Chain;

namespace Paprika.Store;

/// <summary>
/// A dense representation of db addresses
/// </summary>
public class DbAddressSet : IDisposable
{
    private readonly DbAddress _max;
    private static readonly BufferPool _pool = new(128, BufferPool.PageTracking.None);
    private readonly BitSet[] _bitSets;

    private const int BitsPerByte = 8;
    private const int AddressesPerPage = Page.PageSize * BitsPerByte;
    private const int MemoryPerPage = AddressesPerPage * Page.PageSize;

    public DbAddressSet(DbAddress max)
    {
        _max = max;
        var pageCount = max / AddressesPerPage + 1;

        _bitSets = new BitSet[pageCount];
        for (var i = 0; i < pageCount; i++)
        {
            var page = _pool.Rent(false);
            var set = new BitSet(page);
            _bitSets[i] = set;
            set.SetAll();
        }
    }

    public IEnumerable<DbAddress> EnumerateSet()
    {
        for (var i = 0; i < _bitSets.Length; i++)
        {
            var set = _bitSets[i];
            foreach (var index in set.EnumerateSet())
            {
                var addr = DbAddress.Page((uint)(i * AddressesPerPage + index));
                if (addr >= _max)
                    yield break;

                yield return addr;
            }
        }
    }

    public bool this[DbAddress addr]
    {
        get
        {
            var (pageNo, i) = Math.DivRem(addr.Raw, AddressesPerPage);
            return _bitSets[pageNo][(int)i];
        }
        set
        {
            var (pageNo, i) = Math.DivRem(addr.Raw, AddressesPerPage);
            _bitSets[pageNo][(int)i] = value;
        }
    }

    private readonly struct BitSet(Page page)
    {
        public bool this[int i]
        {
            get
            {
                ref var slot = ref GetSlot(i, out var bitMask);
                return (slot & bitMask) == bitMask;
            }
            set
            {
                ref var slot = ref GetSlot(i, out var bitMask);
                if (value)
                {
                    slot = (byte)(slot | bitMask);
                }
                else
                {
                    slot = (byte)(slot & ~bitMask);
                }
            }
        }

        public bool AnySet => page.Span.IndexOfAnyExcept((byte)0) != -1;

        public IEnumerable<int> EnumerateSet()
        {
            if (page.Span.IndexOfAnyExcept((byte)0) == -1)
            {
                yield break;
            }

            for (int i = 0; i < AddressesPerPage; i++)
            {
                if (this[i])
                {
                    yield return i;
                }
            }
        }

        private unsafe ref byte GetSlot(int i, out int bitMask)
        {
            Debug.Assert(i < AddressesPerPage);

            var (atByte, atBit) = Math.DivRem(i, BitsPerByte);

            Debug.Assert(atBit < BitsPerByte);
            bitMask = 1 << atBit;

            return ref Unsafe.Add(ref Unsafe.AsRef<byte>(page.Raw.ToPointer()), atByte);
        }

        public void SetAll()
        {
            MemoryMarshal.Cast<byte, ulong>(page.Span).Fill(0xFF_FF_FF_FF_FF_FF_FF_FF);
        }

        public void Return(BufferPool pool) => pool.Return(page);
    }

    public void Dispose()
    {
        foreach (var set in _bitSets)
        {
            set.Return(_pool);
        }
    }
}
