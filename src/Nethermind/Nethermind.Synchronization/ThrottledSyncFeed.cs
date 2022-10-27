using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization;

public class ThrottledSyncFeed<T>: ISyncFeed<T>
{
    private ISyncFeed<T> _syncFeedImplementation;
    private IBlockProcessingQueue _blockProcessingQueue;
    public int FeedId => _syncFeedImplementation.FeedId;

    public SyncFeedState CurrentState => _syncFeedImplementation.CurrentState;

    public ThrottledSyncFeed(ISyncFeed<T> syncFeedImplementation, IBlockProcessingQueue blockProcessingQueue)
    {
        _syncFeedImplementation = syncFeedImplementation;
        _blockProcessingQueue = blockProcessingQueue;
    }

    public event EventHandler<SyncFeedStateEventArgs>? StateChanged
    {
        add => _syncFeedImplementation.StateChanged += value;
        remove => _syncFeedImplementation.StateChanged -= value;
    }

    public Task<T> PrepareRequest(CancellationToken token = default)
    {
        return _syncFeedImplementation.PrepareRequest(token);
    }

    public async ValueTask<SyncResponseHandlingResult> HandleResponse(T response, PeerInfo peer = null)
    {
        await _blockProcessingQueue.Emptied();
        return await _syncFeedImplementation.HandleResponse(response, peer);
    }

    public bool IsMultiFeed => _syncFeedImplementation.IsMultiFeed;

    public AllocationContexts Contexts => _syncFeedImplementation.Contexts;

    public void Activate()
    {
        _syncFeedImplementation.Activate();
    }

    public void Finish()
    {
        _syncFeedImplementation.Finish();
    }

    public Task FeedTask => _syncFeedImplementation.FeedTask;

    public void Dispose()
    {
        _syncFeedImplementation.Dispose();
    }
}

public static class ThrottledSyncFeedExtension {
    public static ThrottledSyncFeed<T> ThrottleOnBlockProcessing<T>(this ISyncFeed<T> syncFeed,
        IBlockProcessingQueue blockProcessingQueue)
    {
        return new ThrottledSyncFeed<T>(syncFeed, blockProcessingQueue);
    }
}
