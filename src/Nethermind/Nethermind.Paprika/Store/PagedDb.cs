//#define TRACKING_REUSED_PAGES

using NonBlocking;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Paprika.Chain;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store.PageManagers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Paprika.Store;

/// <summary>
/// The base class for page db implementations.
/// </summary>
/// <remarks>
/// Assumes a continuous memory allocation as it provides addressing based on the pointers.
/// </remarks>
public sealed class PagedDb : IPageResolver, IDb, IDisposable
{
    /// <summary>
    /// The number of roots kept in the history.
    /// At least two are required to make sure that the writing transaction does not overwrite the current root.
    /// </summary>
    /// <remarks>
    /// REORGS
    /// It can be set arbitrary big and used for handling reorganizations.
    /// If history depth is set to the max reorg depth, moving to previous block is just a single write transaction moving the root back.
    /// </remarks>
    private const int MinHistoryDepth = 2;

    private readonly IPageManager _manager;
    private readonly byte _historyDepth;
    private long _lastRoot;
    private readonly RootPage[] _roots;

    // batches
    private readonly object _batchLock = new();
    private readonly List<ReadOnlyBatch> _batchesReadOnly = new();
    private Batch? _batchCurrent;
    private bool _disposed;
    private Exception? _commitFailure;

    // pooled objects
    private Context? _ctx;
    private readonly BufferPool _pooledRoots;

#if TRACKING_REUSED_PAGES
    // reuse tracking
    private readonly Dictionary<DbAddress, uint> _registeredForReuse = new();
#endif

    /// <summary>
    /// Initializes the paged db.
    /// </summary>
    /// <param name="manager">The page manager.</param>
    /// <param name="historyDepth">The depth history represent how many blocks should be able to be restored from the past. Effectively,
    ///     a reorg depth. At least 2 are required</param>
    public PagedDb(IPageManager manager, byte historyDepth)
    {
        _manager = manager;
        _historyDepth = historyDepth;
        _roots = new RootPage[historyDepth];
        _batchCurrent = null;
        _ctx = new Context();

        try
        {
            if (historyDepth < MinHistoryDepth)
                throw new ArgumentException($"{nameof(historyDepth)} should be bigger than {MinHistoryDepth}");

            RootInit();
        }
        catch
        {
            // The db owns the manager, so a store that fails to open does not keep its file open.
            _ctx.Free();
            manager.Dispose();
            throw;
        }

        // Pool
        _pooledRoots = new BufferPool(16, BufferPool.PageTracking.AssertCount);
    }

    public static PagedDb NativeMemoryDb(long size, byte historyDepth = 2) =>
        new(new NativeMemoryPageManager(size, historyDepth), historyDepth);

    public static PagedDb MemoryMappedDb(long size, byte historyDepth, string directory, IPageAccessObserver? accessObserver,
        bool flushToDisk = true) =>
        new(
            new MemoryMappedPageManager(size, historyDepth, directory, accessObserver,
                flushToDisk ? PersistenceOptions.FlushFile : PersistenceOptions.MMapOnly), historyDepth);

    public void Prefetch(DbAddress address) => _manager.Prefetch(address);

    private void RootInit()
    {
        // create all root pages for the history depth
        for (uint i = 0; i < _historyDepth; i++)
        {
            _roots[i] = new RootPage(_manager.GetAt(DbAddress.Page(i)));
        }

        if (Array.TrueForAll(_roots, root => root.AsPage().Span.IndexOfAnyExcept((byte)0) < 0))
        {
            InitFresh();
            return;
        }

        _lastRoot = -1;
        for (var i = 0; i < _historyDepth; i++)
        {
            ref readonly var root = ref _roots[i];

            // A torn root is skipped: it was never visible, and the root before it is intact.
            if (root.IsSealed == false)
            {
                continue;
            }

            root.Data.Format.ThrowIfIncompatible(_historyDepth);

            var batchId = root.Header.BatchId;
            if (batchId % _historyDepth == i && batchId > _lastRoot)
            {
                _lastRoot = batchId;
            }
        }

        if (_lastRoot < 0)
        {
            // Only the first commit writes every root, the new store's stamp and its own, so only it can tear them all.
            // It never returned, so nothing it wrote was visible.
            if (Array.TrueForAll(_roots, root => root.Header.BatchId <= 1))
            {
                InitFresh();
                return;
            }

            throw new InvalidOperationException(
                "The file is not a store of this format or is corrupt: none of its root pages is intact.");
        }

        if (Root.Data.NextFreePage.Raw > _manager.MaxPage)
        {
            throw new InvalidOperationException(
                $"The store is corrupt: its root uses {Root.Data.NextFreePage.Raw} pages, but the file holds {_manager.MaxPage}.");
        }
    }

    private void InitFresh()
    {
        foreach (var root in _roots)
        {
            root.AsPage().Clear();
        }

        ref var first = ref _roots[0].Data;
        first.NextFreePage = DbAddress.Page(_historyDepth);
        first.Format = RootFormat.Create(_historyDepth);
        _roots[0].Seal();

        _lastRoot = 0;
    }

    public double Megabytes
    {
        get
        {
            lock (_batchLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return GetRootSizeInMb(Root);
            }
        }
    }

    private RootPage Root => _roots[_lastRoot % _historyDepth];

    public uint NextFreePage
    {
        get
        {
            lock (_batchLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return Root.Data.NextFreePage.Raw;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Later attempts to begin a batch or read the store throw <see cref="ObjectDisposedException"/>. While a batch is
    /// still open, the memory stays mapped, and a file locked, until the process exits, as unmapping it would make the
    /// batch fault on its next read.
    /// </remarks>
    public void Dispose()
    {
        lock (_batchLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_batchCurrent != null || _batchesReadOnly.Count > 0)
            {
                return;
            }
        }

        _ctx?.Free();
        _pooledRoots.Dispose();
        _manager.Dispose();
    }

    /// <summary>
    /// Begins a batch representing the next block.
    /// </summary>
    /// <returns></returns>
    public IBatch BeginNextBatch() => BuildFromRoot();

    IReadOnlyBatch IDb.BeginReadOnlyBatch(string name) => BeginReadOnlyBatch(name);

    public IVisitableReadOnlyBatch BeginReadOnlyBatch(string name = "")
    {
        lock (_batchLock)
        {
            var root = Root;
            return BeginReadOnlyBatch(name, root);
        }
    }

    public int CountReadOnlyBatches()
    {
        lock (_batchLock)
        {
            return _batchesReadOnly.Count;
        }
    }

    /// <summary>
    /// The number of open read-only batches, the oldest batch they read, and the last committed batch.
    /// </summary>
    /// <remarks>
    /// A reader keeps the pages of its batch from being reused, so <c>LastBatchId - OldestReaderBatchId</c> is how far
    /// behind it holds the store. With no reader open, the oldest batch is the last one.
    /// </remarks>
    public (int OpenReaders, uint OldestReaderBatchId, uint LastBatchId) GetReaderStats()
    {
        lock (_batchLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var last = Root.Header.BatchId;
            var oldest = last;
            foreach (var reader in _batchesReadOnly)
            {
                oldest = Math.Min(oldest, reader.BatchId);
            }

            return (_batchesReadOnly.Count, oldest, last);
        }
    }

    private ReadOnlyBatch BeginReadOnlyBatch(string name, in RootPage root)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var copy = new RootPage(_pooledRoots.Rent(false));

        root.CopyTo(copy);

        var batch = new ReadOnlyBatch(this, copy, name);

        _batchesReadOnly.Add(batch);

        return batch;
    }

    public IReadOnlyBatch BeginReadOnlyBatch(in Keccak stateHash, string name = "")
    {
        lock (_batchLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            for (var back = 0; back < _historyDepth; back++)
            {
                if (_lastRoot - back < 0)
                {
                    break;
                }

                var at = (_lastRoot - back) % _historyDepth;
                ref readonly var root = ref _roots[at];

                if (root.Data.Metadata.StateHash == stateHash)
                {
                    return BeginReadOnlyBatch(name, root);
                }
            }

            ThrowNoMatchingPage(stateHash);
            return null!;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowNoMatchingPage(in Keccak stateHash)
        {
            throw new Exception($"There is no root page with the given stateHash '{stateHash}'!");
        }
    }

    public IReadOnlyBatch BeginReadOnlyBatchOrLatest(in Keccak stateHash, string name = "")
    {
        lock (_batchLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            for (var back = 0; back < _historyDepth; back++)
            {
                if (_lastRoot - back < 0)
                {
                    break;
                }

                var at = (_lastRoot - back) % _historyDepth;
                ref readonly var root = ref _roots[at];

                if (root.Data.Metadata.StateHash == stateHash)
                {
                    return BeginReadOnlyBatch(name, root);
                }
            }

            return BeginReadOnlyBatch(name, Root);
        }
    }

    public IReadOnlyBatch[] SnapshotAll()
    {
        var batches = new List<IReadOnlyBatch>();

        lock (_batchLock)
        {
            for (var back = 0; back < _historyDepth; back++)
            {
                if (_lastRoot - back < 0)
                {
                    break;
                }

                var at = (_lastRoot - back) % _historyDepth;

                batches.Add(BeginReadOnlyBatch(nameof(SnapshotAll), _roots[at]));
            }
        }

        return batches.ToArray();
    }

    public bool HasState(in Keccak stateHash)
    {
        lock (_batchLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            for (var back = 0; back < _historyDepth; back++)
            {
                if (_lastRoot - back < 0)
                {
                    break;
                }

                var at = (_lastRoot - back) % _historyDepth;
                ref readonly var root = ref _roots[at];

                if (root.Data.Metadata.StateHash == stateHash)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public int HistoryDepth => _historyDepth;

    public void Accept(IPageVisitor visitor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var i = 0U;

        foreach (var root in _roots)
        {
            using (visitor.On(root, DbAddress.Page(i++)))
            {
                root.Accept(visitor, this);
            }
        }
    }

    public void VisitRoot(IPageVisitor visitor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var root = Root;

        using (visitor.On(root, GetAddress(Root.AsPage())))
        {
            root.Accept(visitor, this);
        }
    }

    /// <summary>
    /// Allows to walk through the pages. No locks, no safety, use for dev purposes.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<Page> UnsafeEnumerateNonRoot()
    {
        for (uint i = _historyDepth; i < Root.Data.NextFreePage; i++)
        {
            yield return _manager.GetAt(DbAddress.Page(i));
        }
    }

    private static int GetRootSizeInMb(RootPage root) =>
        (int)((long)root.Data.NextFreePage.Raw * Page.PageSize / 1024 / 1024);

    private void DisposeReadOnlyBatch(ReadOnlyBatch batch)
    {
        lock (_batchLock)
        {
            _batchesReadOnly.Remove(batch);
            _pooledRoots.Return(batch.Root.AsPage());
        }
    }

    private IBatch BuildFromRoot()
    {
        lock (_batchLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_commitFailure != null)
            {
                ThrowPoisoned(_commitFailure);
            }

            if (_batchCurrent != null)
            {
                ThrowOnlyOneBatch();
            }

            var ctx = _ctx ?? new Context();
            _ctx = null;

            // prepare root
            var root = new RootPage(ctx.Page);
            Root.CopyTo(root);

            // always inc the batchId
            root.Header.BatchId++;

            // select min batch across the one respecting history and the min of all the read-only batches
            var rootBatchId = root.Header.BatchId;

            var minBatch = rootBatchId < _historyDepth ? 0 : rootBatchId - _historyDepth;
            foreach (var batch in _batchesReadOnly)
            {
                minBatch = Math.Min(batch.BatchId, minBatch);
            }

            return _batchCurrent = new Batch(this, root, minBatch, ctx);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowOnlyOneBatch()
        {
            throw new Exception("There is another batch active at the moment. Commit the other first");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowPoisoned(Exception commitFailure)
        {
            throw new InvalidOperationException(
                "The database is poisoned: a commit failed, so the state of the file is unknown. Restart to recover.",
                commitFailure);
        }
    }

    private DbAddress GetAddress(in Page page) => _manager.GetAddress(page);

    public Page GetAt(DbAddress address) => _manager.GetAt(address);

    private Page GetAtForWriting(DbAddress address, bool reused) => _manager.GetAtForWriting(address, reused);

    /// <summary>
    /// Sets the new root but does not bump the _lastRoot that should be done in a lock.
    /// </summary>
    private DbAddress SetNewRoot(RootPage root)
    {
        var pageAddress = (_lastRoot + 1) % _historyDepth;

        root.Seal();
        root.CopyTo(_roots[pageAddress]);
        return DbAddress.Page((uint)pageAddress);
    }

    private void CommitNewRoot() => _lastRoot += 1;


    private sealed class ReadOnlyBatch(PagedDb db, RootPage root, string name)
        : IVisitableReadOnlyBatch, IReadOnlyBatchContext
    {
        [ThreadStatic] private static ConcurrentDictionary<Keccak, ContractId>? s_cache;

        private ConcurrentDictionary<Keccak, ContractId> _idCache = Interlocked.Exchange(ref s_cache, null) ?? new(
            Environment.ProcessorCount,
            RootPage.IdCacheLimit);

        public RootPage Root => root;

        private volatile bool _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, true))
            {
                return;
            }

            db.DisposeReadOnlyBatch(this);

            ReturnCacheToPool();

            void ReturnCacheToPool()
            {
                var idCache = _idCache;
                _idCache = null!;
                ref var cache = ref s_cache;
                if (cache is null)
                {
                    // Return the cache to be reused
                    idCache.Clear();
                    cache = idCache;
                }
            }
        }

        public Metadata Metadata => root.Data.Metadata;

        public bool TryGet(scoped in Key key, out ReadOnlySpan<byte> result)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return root.TryGet(key, this, out result);
        }

        public void VerifyNoPagesMissing() => MissingPagesVisitor.VerifyNoPagesMissing(Root, db, this);

        public void Accept(IPageVisitor visitor) => Root.Accept(visitor, this);

        public RangeIterator IterateState(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RangeIterator.OverState(root, this, IsDisposed, from, to);
        }

        public RangeIterator IterateStorage(ReadOnlySpan<byte> account, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RangeIterator.OverStorage(root, this, IsDisposed, account, from, to);
        }

        public RangeIterator IterateContracts(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RangeIterator.OverContracts(root, this, IsDisposed, from, to);
        }

        private bool IsDisposed() => _disposed;

        public uint BatchId => root.Header.BatchId;

        public IDictionary<Keccak, ContractId> IdCache
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _idCache;
            }
        }

        public void Prefetch(DbAddress address) => db.Prefetch(address);

        public Page GetAt(DbAddress address)
        {
            root.Assert(address);
            return db._manager.GetAt(address);
        }

        public override string ToString() => $"{nameof(ReadOnlyBatch)}, Name: {name}, BatchId: {BatchId}";
    }

    class Batch : BatchContextBase, IBatch
    {
        private readonly PagedDb _db;
        private readonly RootPage _root;
        private readonly uint _reusePagesOlderThanBatchId;
        private bool _verify = false;
        private bool _disposed;
        private bool _commitStarted;
        private bool _writeFailed;

        private readonly Context _ctx;

        /// <summary>
        /// A pool of pages that are abandoned during this batch.
        /// </summary>
        private readonly List<DbAddress> _abandoned;

        /// <summary>
        /// The set of pages written during this batch.
        /// </summary>
        private readonly HashSet<DbAddress> _written;

        /// <summary>
        /// Pages that can be reused immediately.
        /// </summary>
        private readonly Stack<DbAddress> _reusedImmediately;

        public Batch(PagedDb db, RootPage root, uint reusePagesOlderThanBatchId, Context ctx) : base(
            root.Header.BatchId)
        {
            _db = db;
            _root = root;
            _reusePagesOlderThanBatchId = reusePagesOlderThanBatchId;
            _ctx = ctx;
            _abandoned = ctx.Abandoned;
            _written = ctx.Written;
            _reusedImmediately = ctx.ReusedImmediately;

            IdCache = ctx.IdCache;
        }

        public Metadata Metadata => _root.Data.Metadata;

        public bool TryGet(scoped in Key key, out ReadOnlySpan<byte> result)
        {
            // A committed batch is not a reader, so its pages can be reused.
            CheckDisposed();

            return _root.TryGet(key, this, out result);
        }

        public void VerifyNoPagesMissing() => MissingPagesVisitor.VerifyNoPagesMissing(_root, _db, this);

        public void SetMetadata(uint blockNumber, in Keccak blockHash)
        {
            CheckDisposed();
            _root.Data.Metadata = new Metadata(blockNumber, blockHash);
        }

        public void SetRaw(in Key key, ReadOnlySpan<byte> rawData)
        {
            CheckDisposed();

            try
            {
                _root.SetRaw(key, this, rawData);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        public void Destroy(in NibblePath account)
        {
            _root.Destroy(this, account);
        }

        public void DeleteByPrefix(in Key prefix)
        {
            CheckDisposed();

            try
            {
                _root.DeleteByPrefix(in prefix, this);
            }
            catch
            {
                _writeFailed = true;
                throw;
            }
        }

        public void Clear()
        {
            CheckDisposed();

            // Every page this batch took or abandoned so far is in the reclaim range; tracking it too would hand it out twice.
            _abandoned.Clear();
            _reusedImmediately.Clear();
            IdCache.Clear();

            _root.Data.Clear(BatchId, _db._historyDepth);
        }

        private void CheckDisposed()
        {
            if (_disposed || _commitStarted)
            {
                ThrowObjectDisposed();
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowObjectDisposed()
            {
                throw new ObjectDisposedException("This batch has been disposed or committed already.");
            }
        }

        public async ValueTask Commit(CommitOptions options)
        {
            CheckDisposed();

            if (_writeFailed)
            {
                ThrowWriteFailed();
            }

            // Retrying would register the abandoned pages again, handing each of them out twice.
            _commitStarted = true;

            try
            {
                await CommitOnce(options);
            }
            catch (Exception exception)
            {
                lock (_db._batchLock)
                {
                    _db._commitFailure = exception;
                }

                throw;
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowWriteFailed()
            {
                // A copied page is registered for reuse before its parent points at the copy.
                throw new InvalidOperationException(
                    "A write to this batch failed, which may have left its pages registered for reuse while still in use. Dispose it without committing.");
            }
        }

        private async ValueTask CommitOnce(CommitOptions options)
        {
            // memoize the abandoned so that it's preserved for future uses
            MemoizeAbandoned();

            if (_verify)
            {
                VerifyNoPagesMissing();
            }

            await _db._manager.WritePages(_written, options);

            var newRootPage = _db.SetNewRoot(_root);

            await _db._manager.WriteRootPage(newRootPage, options);

            lock (_db._batchLock)
            {
                _db.CommitNewRoot();
                Debug.Assert(ReferenceEquals(this, _db._batchCurrent));
                _db._batchCurrent = null;
            }
        }

        public void VerifyDbPagesOnCommit()
        {
            _verify = true;
        }

        [DebuggerStepThrough]
        public override Page GetAt(DbAddress address)
        {
            // Getting a page beyond root!
            _root.Assert(address);
            var page = _db.GetAt(address);
            return page;
        }

        public override void Prefetch(DbAddress address) => _db.Prefetch(address);

        public override DbAddress GetAddress(Page page) => _db.GetAddress(page);

        public override Page GetNewPage(out DbAddress addr, bool clear)
        {
            bool reused;
            if (_reusedImmediately.TryPop(out addr))
            {
                reused = true;
            }
            else if (TryGetNoLongerUsedPage(out addr))
            {
                reused = true;
            }
            else if (_root.Data.Reclaim.TryTake(_reusePagesOlderThanBatchId, out addr))
            {
                reused = true;
            }
            else
            {
                reused = false;

                // on failure to reuse a page, default to allocating a new one.
                addr = _root.Data.GetNextFreePage();
            }

            var page = _db.GetAtForWriting(addr, reused);

            // if (reused)
            // {
            //     Debug.Assert(page.Header.PageType == PageType.Abandoned || page.Header.BatchId <= BatchId - _db._historyDepth,
            //         $"The page at {addr} is reused at batch {BatchId} even though it was recently written at {page.Header.BatchId}. " +
            //         $"Only {nameof(PageType.Abandoned)} can be reused in that manner.");
            // }

            if (clear)
            {
                page.Clear();
            }

            _written.Add(addr);

            // Clear whole header first
            page.Header = default;

            AssignBatchId(page);
            return page;
        }

        private void MemoizeAbandoned()
        {
            if (_reusedImmediately.Count > 0)
            {
                _abandoned.AddRange(_reusedImmediately);
                _reusedImmediately.Clear();
            }

            if (_abandoned.Count == 0)
            {
                // nothing to memoize
                return;
            }

            _root.Data.AbandonedList.Register(_abandoned, this);
        }

        private bool TryGetNoLongerUsedPage(out DbAddress found)
        {
            var claimed = _root.Data.AbandonedList.TryGet(out found, _reusePagesOlderThanBatchId, this);

#if TRACKING_REUSED_PAGES
            if (claimed)
            {
                if (_db._registeredForReuse.Remove(found) == false)
                {
                    throw new Exception(
                        $"The page {found} is not registered as reusable. Must have been taken before. It's tried to be reused again at batch {BatchId}.");
                }
            }
#endif

            return claimed;
        }

        private void RegisterForFutureReuse(DbAddress addr)
        {
#if TRACKING_REUSED_PAGES
            // register at this batch
            ref var batchId =
 ref CollectionsMarshal.GetValueRefOrAddDefault(_db._registeredForReuse, addr, out var exists);
            if (exists)
            {
                throw new Exception(
                    $"The page {addr} that is tried to be registered for reuse at batch {BatchId} has already been registered at batch {batchId}");
            }

            batchId = BatchId;
#endif

            _abandoned.Add(addr);
        }

        public override void RegisterForFutureReuse(Page page, bool possibleImmediateReuse = false)
        {
            var addr = _db.GetAddress(page);

            if (page.Header.BatchId == BatchId)
            {
                if (possibleImmediateReuse)
                {
                    _reusedImmediately.Push(addr);
                    return;
                }
            }

            RegisterForFutureReuse(addr);
        }

        public override void NoticeAbandonedPageReused(Page page)
        {
            var addr = _db.GetAddress(page);
#if TRACKING_REUSED_PAGES
            if (_db._registeredForReuse.Remove(addr) == false)
            {
                throw new Exception($"The page {addr} should have been registered as registered for the reuse but it has not.");
            }
#endif
            // The page is changed through the mapping, which flushing the file does not cover on Windows.
            _written.Add(addr);
        }

        public override Dictionary<Keccak, ContractId> IdCache { get; }

        public void Dispose()
        {
            // Disposing again would return the context once more, while the next batch may already use it.
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            lock (_db._batchLock)
            {
                if (ReferenceEquals(_db._batchCurrent, this))
                {
                    _db._batchCurrent = null;
                }

                // clear and return
                _ctx.Clear();
                _db._ctx = _ctx;
            }
        }
    }

    /// <summary>
    /// A reusable context for the write batch.
    /// </summary>
    private sealed class Context
    {
        public unsafe Context()
        {
            Page = new((byte*)NativeMemory.AlignedAlloc(Page.PageSize, (UIntPtr)UIntPtr.Size));
            Abandoned = new List<DbAddress>();
            Written = new HashSet<DbAddress>();
            IdCache = new Dictionary<Keccak, ContractId>();
            ReusedImmediately = new Stack<DbAddress>();
        }

        public Dictionary<Keccak, ContractId> IdCache { get; }

        public Page Page { get; }

        public List<DbAddress> Abandoned { get; }
        public HashSet<DbAddress> Written { get; }

        public Stack<DbAddress> ReusedImmediately { get; }

        public void Clear()
        {
            Abandoned.Clear();
            Written.Clear();
            IdCache.Clear();
            Abandoned.Clear();
            ReusedImmediately.Clear();

            // no need to clear, it's always overwritten
            //Page.Clear();
        }

        public unsafe void Free() => NativeMemory.AlignedFree(Page.Raw.ToPointer());
    }

    public void Flush() => _manager.Flush();

    public void ForceFlush() => _manager.ForceFlush();
}

internal class MissingPagesVisitor : IPageVisitor, IDisposable
{
    private readonly DbAddressSet _pages;

    public static void VerifyNoPagesMissing(RootPage root, PagedDb db, IReadOnlyBatchContext context)
    {
        using var missing = new MissingPagesVisitor(root, db.HistoryDepth);
        root.Accept(missing, db);
        missing.EnsureNoMissing(context);
    }

    private MissingPagesVisitor(RootPage page, int historyDepth)
    {
        _pages = new(page.Data.NextFreePage);

        // Mark all roots
        for (uint i = 0; i < historyDepth; i++)
        {
            Mark(DbAddress.Page(i));
        }

        // Mark the pages dropped by a clear that are not reused yet
        var reclaim = page.Data.Reclaim;
        for (var addr = reclaim.Next; addr < reclaim.End; addr = addr.Next)
        {
            Mark(addr);
        }
    }

    public IDisposable On<TPage>(scoped ref NibblePath.Builder prefix, TPage page, DbAddress addr)
        where TPage : unmanaged, IPage
    {
        if (typeof(TPage) == typeof(AbandonedPage))
        {
            return On(As<TPage, AbandonedPage>(page), addr);
        }

        return Mark(addr);
    }

    public IDisposable On<TPage>(TPage page, DbAddress addr) where TPage : unmanaged, IPage
    {
        if (typeof(TPage) == typeof(AbandonedPage))
        {
            return On(As<TPage, AbandonedPage>(page), addr);
        }

        return Mark(addr);
    }

    public IDisposable Scope(string name) => NoopDisposable.Instance;

    private static TDestinationPage As<TPage, TDestinationPage>(in TPage page)
        where TDestinationPage : IPage
    {
        return Unsafe.As<TPage, TDestinationPage>(ref Unsafe.AsRef(in page));
    }

    public IDisposable On(AbandonedPage page, DbAddress addr)
    {
        foreach (var abandoned in page.Enumerate())
        {
            Mark(abandoned);
        }

        return Mark(addr);
    }

    private IDisposable Mark(DbAddress addr)
    {
        _pages[addr] = false;
        return NoopDisposable.Instance;
    }

    public void Dispose() => _pages.Dispose();

    public void EnsureNoMissing(IReadOnlyBatchContext batch)
    {
        foreach (var addr in _pages.EnumerateSet())
        {
            var page = batch.GetAt(addr);
            throw new Exception(
                $"The page at {addr} is not reachable from the tree nor from the set of abandoned pages. " +
                $"Highly likely it's a leak. The page is {page.Header.PageType} and was written last in batch {page.Header.BatchId} " +
                $"while the current batch is {batch.BatchId}");
        }
    }
}

public interface IVisitableReadOnlyBatch : IReadOnlyBatch, IVisitable
{
    /// <summary>
    /// Iterates, in ascending order, the state entries whose key is exactly <paramref name="from"/>.Length bytes long and
    /// in [<paramref name="from"/>, <paramref name="to"/>].
    /// </summary>
    /// <remarks>
    /// Both bounds have the same length, 1 to 32 bytes. Keys of any other length, such as metadata keys, are skipped.
    /// </remarks>
    RangeIterator IterateState(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to);

    /// <summary>
    /// Iterates, in ascending order, the storage cells of the contract keyed by the 32-byte <paramref name="account"/>
    /// whose 32-byte slot is in [<paramref name="from"/>, <paramref name="to"/>].
    /// </summary>
    RangeIterator IterateStorage(ReadOnlySpan<byte> account, ReadOnlySpan<byte> from, ReadOnlySpan<byte> to);

    /// <summary>
    /// Iterates, in ascending order, the 32-byte keys in [<paramref name="from"/>, <paramref name="to"/>] of the contracts
    /// that own a storage id, that is whose storage was ever written.
    /// </summary>
    /// <remarks>The value of each is its id, 4 bytes little-endian.</remarks>
    RangeIterator IterateContracts(ReadOnlySpan<byte> from, ReadOnlySpan<byte> to);
}
