using System.Runtime.InteropServices;

namespace Paprika.Store.PageManagers;

public sealed unsafe class NativeMemoryPageManager : PointerPageManager
{
    private readonly void* _ptr;

    public NativeMemoryPageManager(long size, byte historyDepth) : base(size, historyDepth, accessObserver: null)
    {
        _ptr = NativeMemory.AlignedAlloc((UIntPtr)size, (UIntPtr)Page.PageSize);

        // clear first pages to make it clean
        for (var i = 0; i < historyDepth; i++)
        {
            GetAt(new DbAddress((uint)i)).Clear();
        }
    }

    protected override void* Ptr => _ptr;

    public override void Flush()
    {
    }

    public override void ForceFlush()
    {
    }

    public override bool UsesPersistentPaging => false;

    public override void Dispose() => NativeMemory.AlignedFree(_ptr);

    public override ValueTask WritePages(ICollection<DbAddress> addresses, CommitOptions options) =>
        ValueTask.CompletedTask;

    public override ValueTask WriteRootPage(DbAddress rootPage, CommitOptions options) => ValueTask.CompletedTask;
}
