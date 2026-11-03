using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Paprika.Utils;

namespace Paprika.Store.PageManagers;

public sealed class MemoryMappedPageManager : PointerPageManager
{
    private readonly PersistenceOptions _options;

    /// <summary>
    /// The only option is random access. As Paprika jumps over the file, any prefetching is futile.
    /// So here it is, random access file.
    /// </summary>
    private const FileOptions PaprikaFileOptions = FileOptions.RandomAccess;

    private const string PaprikaFileName = "paprika.db";

    /// <summary>
    /// How far ahead disk blocks are reserved for new pages, see <see cref="Preallocate"/>.
    /// </summary>
    private const long PreallocationChunk = 256 * 1024 * 1024;

    private readonly long _size;
    private readonly SafeFileHandle _file;
    private readonly MemoryMappedFile _mapped;
    private readonly MemoryMappedViewAccessor _whole;
    private readonly unsafe byte* _ptr;
    private readonly IPageAccessObserver? _accessObserver;

    // Preallocation section, starting from scratch at every open: reserving blocks that exist is cheap.
    private bool _preallocate = OperatingSystem.IsLinux();
    private long _preallocatedEnd;

    private bool _disposed;

    // Flusher section
    private DbAddress[] _toWrite = new DbAddress[1];

    public unsafe MemoryMappedPageManager(long size, byte historyDepth, string dir, IPageAccessObserver? accessObserver,
        PersistenceOptions options = PersistenceOptions.FlushFile) : base(size, historyDepth, accessObserver)
    {
        Path = GetPaprikaFilePath(dir);
        _size = size;
        _accessObserver = accessObserver;

        try
        {
            if (!File.Exists(Path))
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                _file = File.OpenHandle(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, PaprikaFileOptions);

                // Windows allocates the whole length of a file that is not sparse.
                if (OperatingSystem.IsWindows())
                {
                    NativeMethods.MarkSparse(_file);
                }

                // set length
                RandomAccess.SetLength(_file, size);

                // clear first pages to make it clean
                var page = new byte[Page.PageSize];
                for (var i = 0; i < historyDepth; i++)
                {
                    RandomAccess.Write(_file, page, i * Page.PageSize);
                }

                RandomAccess.FlushToDisk(_file);
                NativeMethods.FsyncDirectory(directory);
            }
            else
            {
                _file = File.OpenHandle(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, PaprikaFileOptions);
            }

            _mapped = MemoryMappedFile.CreateFromFile(_file, null, size, MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None, true);

            _whole = _mapped.CreateViewAccessor();
            _whole.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
            NativeMethods.AdviseRandom(_ptr, size);
            accessObserver?.OnMapped((nint)_ptr, size);
        }
        catch
        {
            // The file is locked while it is open, so a failed open must not leave the lock to the finalizer; the view is
            // unmapped only once its pointer is released.
            if (_ptr != null) _whole!.SafeMemoryMappedViewHandle.ReleasePointer();
            _whole?.Dispose();
            _mapped?.Dispose();
            _file?.Dispose();
            throw;
        }

        _options = options;
    }

    public static string GetPaprikaFilePath(string dir) => System.IO.Path.Combine(dir, PaprikaFileName);

    public string Path { get; }

    protected override unsafe void* Ptr => _ptr;

    public override ValueTask WritePages(ICollection<DbAddress> dbAddresses, CommitOptions options)
    {
        if (_options == PersistenceOptions.MMapOnly)
            return ValueTask.CompletedTask;

        if (options != CommitOptions.DangerNoWrite)
        {
            WriteSorted(dbAddresses);
        }

        if (options != CommitOptions.DangerNoFlush && options != CommitOptions.DangerNoWrite)
        {
            RandomAccess.FlushToDisk(_file);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The amount of pages that can be combined in a single write.
    /// </summary>
    private const int MaxWriteBatch = 64;

    private void WriteSorted(ICollection<DbAddress> dbAddresses)
    {
        var count = dbAddresses.Count;
        if (count == 0)
            return;

        if (_toWrite.Length < count)
        {
            Array.Resize(ref _toWrite, count);
        }

        dbAddresses.CopyTo(_toWrite, 0);
        var span = _toWrite.AsSpan(0, count);

        // raw sorting, to make writes ordered
        var numbers = MemoryMarshal.Cast<DbAddress, uint>(span);
        numbers.Sort();

        foreach (var range in numbers.BatchConsecutive(MaxWriteBatch))
        {
            WriteAt(new DbAddress(range.Start), (uint)range.Length);
        }
    }

    /// <summary>
    /// Writes the pages to the file, synchronously.
    /// </summary>
    /// <remarks>
    /// The pages were changed through the shared mapping, which the fsync that follows covers on Linux. Writing them is
    /// still load-bearing: before Linux 6.2, a store through the mapping racing with a <c>MADV_DONTNEED</c> of its page
    /// could be marked clean by writeback and lost, and the write dirties the page again.
    /// </remarks>
    private unsafe void WriteAt(DbAddress addr, uint count = 1)
    {
        var page = GetAt(addr);

        // GetAt reported the first page only, while the write reads the whole run through the mapping.
        for (uint i = 1; i < count; i++)
        {
            _accessObserver?.OnAccess(addr.Raw + i);
        }

        RandomAccess.Write(_file, new ReadOnlySpan<byte>(page.Raw.ToPointer(), (int)(Page.PageSize * count)), addr.FileOffset);
    }

    public override Page GetAtForWriting(DbAddress address, bool reused)
    {
        var page = GetAt(address);

        if (reused == false && _preallocate)
        {
            Preallocate(address);
        }

        return page;
    }

    /// <summary>
    /// Reserves disk blocks from the new page at <paramref name="address"/> onwards, so a full disk fails here with an
    /// <see cref="IOException"/> instead of a SIGBUS when the page is written through the mapping.
    /// </summary>
    /// <remarks>
    /// The file grows only through new pages: reused pages were written by committed batches, so they have blocks.
    /// Copy-on-write file systems such as btrfs and ZFS can still raise SIGBUS.
    /// </remarks>
    private void Preallocate(DbAddress address)
    {
        var offset = address.FileOffset;
        if (offset < _preallocatedEnd)
        {
            return;
        }

        var length = Math.Min(PreallocationChunk, _size - offset);
        if (NativeMethods.TryAllocate(_file, offset, length))
        {
            _preallocatedEnd = offset + length;
        }
        else
        {
            _preallocate = false;
        }
    }

    public override ValueTask WriteRootPage(DbAddress root, CommitOptions options)
    {
        if (_options == PersistenceOptions.MMapOnly)
            return ValueTask.CompletedTask;

        if (options != CommitOptions.DangerNoWrite)
        {
            WriteAt(root);
        }

        if (options == CommitOptions.FlushDataAndRoot)
        {
            RandomAccess.FlushToDisk(_file);
        }

        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
        if (_options == PersistenceOptions.MMapOnly)
            return;

        RandomAccess.FlushToDisk(_file);
    }

    public override void ForceFlush()
    {
        _whole.Flush();
        RandomAccess.FlushToDisk(_file);
    }

    public override bool UsesPersistentPaging => _options == PersistenceOptions.FlushFile;

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

        _accessObserver?.Dispose();
        _whole.SafeMemoryMappedViewHandle.ReleasePointer();
        _whole.Dispose();
        _mapped.Dispose();
        _file.Dispose();
    }
}

public enum PersistenceOptions
{
    /// <summary>
    /// Do FSYNC/FlushFileBuffers
    /// </summary>
    FlushFile,

    /// <summary>
    /// Don't issue any writes or sync.
    /// </summary>
    MMapOnly,
}
