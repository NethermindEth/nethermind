//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.ParallelSync
{
    public abstract class SyncDispatcher<T> : ISyncDispatcher<T>
    {
        private object _feedStateManipulation = new object();
        private SyncFeedState _currentFeedState = SyncFeedState.Dormant;

        private IPeerAllocationStrategyFactory<T> PeerAllocationStrategy { get; }

        protected ILogger Logger { get; }
        protected ISyncFeed<T> Feed { get; }
        protected ISyncPeerPool SyncPeerPool { get; }

        protected SyncDispatcher(ISyncFeed<T> syncFeed, ISyncPeerPool syncPeerPool, IPeerAllocationStrategyFactory<T> peerAllocationStrategy, ILogManager logManager)
        {
            Logger = logManager?.GetClassLogger<SyncDispatcher<T>>() ?? throw new ArgumentNullException(nameof(logManager));
            Feed = syncFeed ?? throw new ArgumentNullException(nameof(syncFeed));
            SyncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            PeerAllocationStrategy = peerAllocationStrategy ?? throw new ArgumentNullException(nameof(peerAllocationStrategy));

            syncFeed.StateChanged += SyncFeedOnStateChanged;
        }

        private TaskCompletionSource<object> _dormantStateTask = new TaskCompletionSource<object>();

        protected abstract Task Dispatch(PeerInfo peerInfo, T request, CancellationToken cancellationToken);

        public async Task Start(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _dormantStateTask?.SetCanceled());

            UpdateState(Feed.CurrentState);
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (_currentFeedState == SyncFeedState.Dormant)
                {
                    Logger.Info($"{GetType().Name} is going to sleep.");
                    await _dormantStateTask.Task;
                    Logger.Info($"{GetType().Name} got acivated.");
                }
                else if (_currentFeedState == SyncFeedState.Active)
                {
                    T request = await (Feed.PrepareRequest() ?? Task.FromResult<T>(default)); // just to avoid null refs
                    if (request == null)
                    {
                        if (!Feed.IsMultiFeed)
                        {
                            Logger.Warn($"{Feed.GetType().Name} enqueued a null request.");
                        }
                        
                        await Task.Delay(50);
                        continue;
                    }

                    SyncPeerAllocation allocation = await Allocate(request);
                    PeerInfo allocatedPeer = allocation.Current; // TryGetCurrent?

                    if (allocatedPeer != null)
                    {
                        Task task = Dispatch(allocatedPeer, request, cancellationToken);
                        if (!Feed.IsMultiFeed)
                        {
                            Logger.Warn($"Awaiting single dispatch from {Feed.GetType().Name} with allocated {allocatedPeer}");
                            await task;
                            Logger.Warn($"Single dispatch from {Feed.GetType().Name} with allocated {allocatedPeer} has been processed");
                        }

#pragma warning disable 4014
                        task.ContinueWith(t =>
#pragma warning restore 4014
                        {
                            if (t.IsFaulted)
                            {
                                if (Logger.IsWarn) Logger.Warn($"Failure when executing request {t.Exception}");
                            }

                            try
                            {
                                // Logger.Warn($"Freeing allocation of {allocatedPeer}");
                                Free(allocation);
                                SyncResponseHandlingResult result = Feed.HandleResponse(request);
                                ReactToHandlingResult(result, allocatedPeer);
                            }
                            catch (Exception e)
                            {
                                // possibly clear the response and handle empty response batch here (to avoid missing parts)
                                // this practically corrupts sync
                                if (Logger.IsError) Logger.Error($"Error when handling response", e);
                            }
                        });
                    }
                    else
                    {
                        SyncResponseHandlingResult result = Feed.HandleResponse(request);
                        ReactToHandlingResult(result, null);
                    }
                }
                else if (_currentFeedState == SyncFeedState.Finished)
                {
                    Logger.Info($"{GetType().Name} has finished work.");
                    break;
                }
            }
        }

        protected virtual void Free(SyncPeerAllocation allocation)
        {
            SyncPeerPool.Free(allocation);
        }

        protected virtual async Task<SyncPeerAllocation> Allocate(T request)
        {
            SyncPeerAllocation allocation = await SyncPeerPool.Allocate(PeerAllocationStrategy.Create(request), string.Empty, 1000);
            return allocation;
        }

        protected virtual void ReactToHandlingResult(SyncResponseHandlingResult result, PeerInfo peer)
        {
            if (peer == null)
            {
                // unassigned
                return;
            }

            switch (result)
            {
                case SyncResponseHandlingResult.Emptish:
                    break;
                case SyncResponseHandlingResult.BadQuality:
                    SyncPeerPool.ReportWeakPeer(peer);
                    break;
                case SyncResponseHandlingResult.InvalidFormat:
                    SyncPeerPool.ReportWeakPeer(peer);
                    break;
                case SyncResponseHandlingResult.NoData:
                    SyncPeerPool.ReportNoSyncProgress(peer);
                    break;
                case SyncResponseHandlingResult.NotAssigned:
                    break;
                case SyncResponseHandlingResult.OK:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result, null);
            }
        }

        private void SyncFeedOnStateChanged(object sender, SyncFeedStateEventArgs e)
        {
            if (!Feed.IsMultiFeed)
            {
                Logger.Warn($"{Feed.GetType().Name} state changed to {e.NewState}");
            }
            
            SyncFeedState state = e.NewState;
            UpdateState(state);
        }

        private void UpdateState(SyncFeedState state)
        {
            lock (_feedStateManipulation)
            {
                if (state == SyncFeedState.Dormant)
                {
                    _dormantStateTask = new TaskCompletionSource<object>();
                    _currentFeedState = state;
                }
                else if (state == SyncFeedState.Active)
                {
                    _currentFeedState = state;
                    _dormantStateTask?.TrySetResult(null);
                    _dormantStateTask = null;
                }
                else if (state == SyncFeedState.Finished)
                {
                    _currentFeedState = state;
                }
            }
        }
    }
}