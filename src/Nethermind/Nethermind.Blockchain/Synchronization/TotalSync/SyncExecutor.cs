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
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization.TotalSync
{
    public abstract class SyncExecutor<T> : ISyncExecutor<T>
    {
        private object _feedStateManipulation = new object();

        private readonly ILogger _logger;
        private readonly ISyncFeed<T> _syncFeed;
        private readonly IPeerSelectionStrategyFactory<T> _peerSelectionStrategy;

        private SyncFeedState _currentFeedState = SyncFeedState.Dormant;
        
        protected IEthSyncPeerPool SyncPeerPool { get; }

        protected SyncExecutor(ISyncFeed<T> syncFeed, IEthSyncPeerPool syncPeerPool, IPeerSelectionStrategyFactory<T> peerSelectionStrategy, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<SyncExecutor<T>>() ?? throw new ArgumentNullException(nameof(logManager));
            _syncFeed = syncFeed ?? throw new ArgumentNullException(nameof(syncFeed));
            SyncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _peerSelectionStrategy = peerSelectionStrategy ?? throw new ArgumentNullException(nameof(peerSelectionStrategy));

            syncFeed.StateChanged += SyncFeedOnStateChanged;
        }

        private TaskCompletionSource<object> _dormantStateTask;

        protected abstract Task Execute(SyncPeerAllocation allocation, T request);

        public async Task Start()
        {
            UpdateState(_syncFeed.CurrentState);
            while (true)
            {
                if (_currentFeedState == SyncFeedState.Dormant)
                {
                    await _dormantStateTask.Task;
                }
                else if (_currentFeedState == SyncFeedState.Active)
                {
                    T request = await _syncFeed.PrepareRequest();
                    if (request == null)
                    {
                        await Task.Delay(50);
                        continue;
                    }
                    
                    SyncPeerAllocation allocation = await SyncPeerPool.Borrow(_peerSelectionStrategy.Create(request), string.Empty, 1000);
                    if (allocation.HasPeer)
                    {
                        Task task = Execute(allocation, request);
#pragma warning disable 4014
                        task.ContinueWith(t =>
#pragma warning restore 4014
                        {
                            if (t.IsFaulted)
                            {
                                if (_logger.IsWarn) _logger.Warn($"Failure when executing request {t.Exception}");
                            }

                            PeerInfo allocatedPeer = allocation.Current;
                            SyncPeerPool.Free(allocation);
                            SyncBatchResponseHandlingResult result = _syncFeed.HandleResponse(request);
                            ReactToHandlingResult(result, allocatedPeer);
                        });
                    }
                }
                else if (_currentFeedState == SyncFeedState.Finished)
                {
                    break;
                }
            }
        }

        protected virtual void ReactToHandlingResult(SyncBatchResponseHandlingResult result, PeerInfo peer)
        {
            switch (result)
            {
                case SyncBatchResponseHandlingResult.Emptish:
                    break;
                case SyncBatchResponseHandlingResult.BadQuality:
                    break;
                case SyncBatchResponseHandlingResult.InvalidFormat:
                    break;
                case SyncBatchResponseHandlingResult.NoData:
                    break;
                case SyncBatchResponseHandlingResult.NotAssigned:
                    break;
                case SyncBatchResponseHandlingResult.OK:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result, null);
            }
        }

        private void SyncFeedOnStateChanged(object sender, SyncFeedStateEventArgs e)
        {
            SyncFeedState state = e.NewState;
            UpdateState(state);
        }

        private void UpdateState(SyncFeedState state)
        {
            lock (_feedStateManipulation)
            {
                _currentFeedState = state;
                if (state == SyncFeedState.Dormant)
                {
                    _dormantStateTask = new TaskCompletionSource<object>();
                }
                else if (state == SyncFeedState.Active)
                {
                    _dormantStateTask?.TrySetResult(null);
                    _dormantStateTask = null;
                }
            }
        }
    }
}