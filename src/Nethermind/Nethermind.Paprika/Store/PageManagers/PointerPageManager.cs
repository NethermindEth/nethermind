using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Paprika.Store.PageManagers;

public abstract unsafe class PointerPageManager(long size, byte historyDepth, IPageAccessObserver? accessObserver) : IPageManager
{
    /// <summary>
    /// The largest size: page addresses have 28 bits, see <see cref="DbAddressList.Max"/>, so 1 TiB.
    /// </summary>
    public static readonly long MaxSize = (DbAddressList.Max.Raw + 1L) * Page.PageSize;

    public int MaxPage { get; } = GetPageCount(size, historyDepth);

    private static int GetPageCount(long size, byte historyDepth)
    {
        if (size % Page.PageSize != 0 || size < (historyDepth + 1L) * Page.PageSize || size > MaxSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size,
                $"The size must be a multiple of {Page.PageSize}, hold the {historyDepth} root pages and at least one more, and be at most {MaxSize} bytes.");
        }

        return (int)(size / Page.PageSize);
    }

    protected abstract void* Ptr { get; }

    public void Prefetch(DbAddress address)
    {
        if (Sse.IsSupported)
        {
            if (address.IsNull || address.Raw > (uint)MaxPage)
            {
                return;
            }

            // Fetch to L2 cache as we don't know if will need it
            // So don't pollute L1 cache
            Sse.Prefetch1((byte*)Ptr + address.FileOffset);
        }
    }

    public Page GetAt(DbAddress address)
    {
        if (address.Raw >= MaxPage)
        {
            ThrowInvalidPage(address.Raw);
        }

        accessObserver?.OnAccess(address.Raw);

        return new Page((byte*)Ptr + address.FileOffset);

        [DoesNotReturn]
        [StackTraceHidden]
        void ThrowInvalidPage(uint raw)
        {
            throw new IndexOutOfRangeException($"The database breached its size! Requested page {raw} from max {MaxPage}. The returned page is invalid");
        }
    }

    public DbAddress GetAddress(in Page page)
    {
        return DbAddress.Page((uint)(Unsafe
            .ByteOffset(ref Unsafe.AsRef<byte>(Ptr), ref Unsafe.AsRef<byte>(page.Raw.ToPointer()))
            .ToInt64() / Page.PageSize));
    }

    public virtual Page GetAtForWriting(DbAddress address, bool reused) => GetAt(address);

    public abstract ValueTask WritePages(ICollection<DbAddress> addresses, CommitOptions options);

    public abstract ValueTask WriteRootPage(DbAddress rootPage, CommitOptions options);

    public abstract void Flush();

    public abstract void ForceFlush();
    public abstract bool UsesPersistentPaging { get; }

    public abstract void Dispose();
}
