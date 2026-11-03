using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Paprika.Store;

namespace Paprika.Chain;

/// <summary>
/// A simple page pool, that creates slabs of pages and allows for their reuse.
/// </summary>
public class BufferPool : IDisposable
{
    public const int BufferSize = Page.PageSize;

    private readonly int _buffersInOneSlab;
    private readonly PageTracking _tracking;

    private readonly ConcurrentQueue<Page> _pool = new();
    private readonly ConcurrentQueue<IntPtr> _slabs = new();
    private readonly ConcurrentDictionary<Page, StackTrace>? _traces;

    public BufferPool(int buffersInOneSlab, PageTracking tracking = PageTracking.AssertCount)
    {
        _buffersInOneSlab = buffersInOneSlab;
        _tracking = tracking;

        if (_tracking == PageTracking.StackTrace)
        {
            _traces = new();
        }
    }

    public unsafe Page Rent(bool clear = true)
    {
        Page pooled;
        while (_pool.TryDequeue(out pooled) == false)
        {
            var allocSize = _buffersInOneSlab * BufferSize;

            var slab = (byte*)NativeMemory.AlignedAlloc((UIntPtr)allocSize, BufferSize);

            _slabs.Enqueue(new IntPtr(slab));

            // enqueue all
            for (var i = 0; i < _buffersInOneSlab; i++)
            {
                var page = new Page(slab + BufferSize * i);
                _pool.Enqueue(page);
            }
        }

        if (clear)
            pooled.Clear();

        if (_traces != null)
        {
            _traces[pooled] = new StackTrace();
        }

        return pooled;
    }

    public void Return(Page page)
    {
        if (_traces != null && _traces.TryRemove(page, out _) == false)
        {
            throw new KeyNotFoundException("The page was not found in the pool.");
        }

        _pool.Enqueue(page);
    }

    public void Dispose()
    {
        var expectedCount = _buffersInOneSlab * _slabs.Count;
        var actualCount = _pool.Count;

        if (_traces != null)
        {
            foreach (var (_, value) in _traces)
            {
                throw new Exception(value.ToString());
            }
        }
        else if (_tracking == PageTracking.AssertCount)
        {
            Debug.Assert(expectedCount == actualCount,
                $"There should be {expectedCount} pages in the pool but there are only {actualCount}");
        }

        while (_slabs.TryDequeue(out var slab))
        {
            unsafe
            {
                NativeMemory.AlignedFree(slab.ToPointer());
            }
        }
    }

    public enum PageTracking
    {
        /// <summary>
        /// No tracking enabled.
        /// </summary>
        None = 0,

        /// <summary>
        /// Count based tracking that throws on missing pages.
        /// </summary>
        AssertCount = 1,

        /// <summary>
        /// Heavy stack capturing tracking
        /// </summary>
        StackTrace = 2
    }
}
