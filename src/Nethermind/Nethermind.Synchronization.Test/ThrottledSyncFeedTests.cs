using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class ThrottledSyncFeedTests
{
    [Test]
    public async Task ThrottledSyncFeed_should_not_handle_request_until_queue_is_empty()
    {
        ISyncFeed<object> baseSyncFeed = Substitute.For<ISyncFeed<object>>();
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();

        TaskCompletionSource processingQueueEmptyTask = new TaskCompletionSource();
        processingQueue.Emptied().Returns(new ValueTask(processingQueueEmptyTask.Task));

        ThrottledSyncFeed<object> throttledSyncFeed = new ThrottledSyncFeed<object>(baseSyncFeed, processingQueue);

        ValueTask<SyncResponseHandlingResult> responseTask = throttledSyncFeed.HandleResponse(new object());
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        await baseSyncFeed.DidNotReceive().HandleResponse(Arg.Any<object>());
        processingQueueEmptyTask.SetResult();

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await baseSyncFeed.Received().HandleResponse(Arg.Any<object>());
    }
}
