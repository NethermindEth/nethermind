using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paprika.Store;

/// <summary>
/// Represents a set of pages abandoned during the batch with the same <see cref="IBatchContext.BatchId"/>
/// as the page. 
/// </summary>
/// <remarks>
/// The structure uses a packed storage so that consecutive numbers that are different by <see cref="PackedFlag"/>
/// are written over a single <see cref="uint"/> using a flag <see cref="PackedFlag"/>.
/// </remarks>
[method: DebuggerStepThrough]
public readonly struct AbandonedPage(Page page) : IPage<AbandonedPage>
{
    private const uint PackedFlag = 0x8000_0000u;
    private const uint PackedDiff = 1;

    private ref PageHeader Header => ref page.Header;
    public uint BatchId => Header.BatchId;
    public DbAddress Next => Data.Next;

    public static int MaxCount => Payload.MaxCount;

    private unsafe ref Payload Data => ref Unsafe.AsRef<Payload>(page.Payload);

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    private struct Payload
    {
        private const int Size = Page.PageSize - PageHeader.Size;

        private const int PageAddressesOffset = sizeof(int) + DbAddress.Size;

        public const int MaxCount = (Size - PageAddressesOffset) / DbAddress.Size;

        /// <summary>
        /// The count of pages contained in this page.
        /// </summary>
        public int Count;

        /// <summary>
        /// The address of the next page that contains information about this <see cref="IBatchContext.BatchId"/>
        /// abandoned pages.
        /// </summary>
        public DbAddress Next;

        private uint AbandonedPages;

        public Span<uint> Abandoned => MemoryMarshal.CreateSpan(ref AbandonedPages, MaxCount);
    }

    public void Accept(IPageVisitor visitor, IPageResolver resolver)
    {
        var addr = Data.Next;

        while (!addr.IsNull)
        {
            var abandoned = new AbandonedPage(resolver.GetAt(addr));
            visitor.On(abandoned, addr);

            addr = abandoned.Next;
        }
    }

    public int CountPages()
    {
        var count = 0;

        for (var i = 0; i < Data.Count; i++)
        {
            var top = Data.Abandoned[i];
            if ((top & PackedFlag) == PackedFlag)
            {
                // is packed, count this and next
                count += 2;
            }
            else
            {
                // not packed, just return
                count += 1;
            }
        }

        return count;
    }

    public IEnumerable<DbAddress> Enumerate()
    {
        for (var i = 0; i < Data.Count; i++)
        {
            var top = Data.Abandoned[i];
            if ((top & PackedFlag) == PackedFlag)
            {
                // is packed, return this and next
                var addr = top & ~PackedFlag;
                yield return new DbAddress(addr);
                yield return new DbAddress(addr + PackedDiff);
            }
            else
            {
                // not packed, just return
                yield return new DbAddress(top);
            }
        }
    }

    public bool TryPeek(out DbAddress addr, out bool hasMoreThanPeeked)
    {
        if (Data.Count == 0)
        {
            addr = default;
            hasMoreThanPeeked = default;
            return false;
        }

        var top = Data.Abandoned[Data.Count - 1];

        if ((top & PackedFlag) == PackedFlag)
        {
            // Remove the flag and return
            addr = new DbAddress((top & ~PackedFlag) + PackedDiff);

            // The entry is packed so has more than peeked.
            hasMoreThanPeeked = true;
            return true;
        }

        // Not packed, handle
        addr = new DbAddress(top);

        // The entry is not packed and is the only one
        hasMoreThanPeeked = Data.Count > 1;

        return true;
    }

    public bool TryPop(out DbAddress addr)
    {
        if (Data.Count == 0)
        {
            addr = default;
            return false;
        }

        ref var top = ref Data.Abandoned[Data.Count - 1];

        if ((top & PackedFlag) == PackedFlag)
        {
            // Remove packed flag
            top &= ~PackedFlag;

            // Return next
            addr = new DbAddress(top + PackedDiff);
            return true;
        }

        // Not packed, handle
        addr = new DbAddress(top);
        Data.Count--;
        return true;
    }

    /// <summary>
    /// Construct the chain of abandoned pages, allowing the abandoned to growth as they are constructed.
    /// </summary>
    public static DbAddress CreateChain(List<DbAddress> abandoned, IBatchContext batch)
    {
        var to = 0;

        DbAddress next = default;

        abandoned.Sort((a, b) => a.Raw.CompareTo(b.Raw));

        while (to < abandoned.Count)
        {
            var page = batch.GetNewCleanPage<AbandonedPage>(out var addr);

            page.Data.Next = next;

            next = addr;

            while (to < abandoned.Count && page.TryPush(abandoned[to]))
            {
                to++;
            }
        }

        return next;
    }

    public bool TryPush(DbAddress address)
    {
        Debug.Assert((address.Raw & PackedFlag) == 0u,
            "Database is over 8TB!");

        ref var count = ref Data.Count;

        if (count == Payload.MaxCount)
        {
            return false;
        }

        if (count == 0)
        {
            // Nothing in the page yet, push the first
            Data.Abandoned[count++] = address;
        }
        else
        {
            ref var last = ref Data.Abandoned[count - 1];

            // If last is not packed and last is previous to the address added, pack it.
            if ((last & PackedFlag) == 0 &&
                last + PackedDiff == address)
            {
                last |= PackedFlag;
            }
            else
            {
                Data.Abandoned[count++] = address;
            }
        }

        return true;
    }

    public void AttachTail(DbAddress tail, IBatchContext batch)
    {
        Debug.Assert(page.Header.BatchId == batch.BatchId);

        if (Data.Next.IsNull)
        {
            Data.Next = tail;
            return;
        }

        var prev = Data.Next;
        var next = new AbandonedPage(batch.GetAt(prev)).Data.Next;

        while (!next.IsNull)
        {
            prev = next;
            next = new AbandonedPage(batch.GetAt(next)).Data.Next;
        }

        new AbandonedPage(batch.GetAt(prev)).Data.Next = tail;
    }

    public void Clear()
    {
        Data.Next = default;
        Data.Count = default;
    }

    public bool IsClean => Data.Next.IsNull && Data.Count == 0;

    public static AbandonedPage Wrap(Page page) => Unsafe.As<Page, AbandonedPage>(ref page);

    public static PageType DefaultType => PageType.Abandoned;
}
