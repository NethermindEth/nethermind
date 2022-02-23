//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class BeaconFullSyncDispatcher
{
    private bool isCanceled;
    private object _feedStateManipulation = new();
    private TaskCompletionSource<object?>? _dormantStateTask = new();
    private SyncFeedState _currentFeedState = SyncFeedState.Dormant;

    private ISyncFeed<BlockHeader?> _feed;
    private ILogger _logger;

    public BeaconFullSyncDispatcher(
        ISyncFeed<BlockHeader?> feed,
        ILogManager logManager)
    {
        _feed = feed;
        _logger = logManager.GetClassLogger();
        _feed.StateChanged += SyncFeedOnStateChanged;
    }

    public async Task Start(CancellationToken cancellationToken)
        {
            cancellationToken.Register(() =>
            {
                lock (_feedStateManipulation)
                {
                    isCanceled = true;
                    _dormantStateTask?.SetCanceled();
                }
            });

            UpdateState(_feed.CurrentState);
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                SyncFeedState currentStateLocal;
                TaskCompletionSource<object?>? dormantTaskLocal;
                lock (_feedStateManipulation)
                {
                    currentStateLocal = _currentFeedState;
                    dormantTaskLocal = _dormantStateTask;
                    if (isCanceled)
                    {
                        break;
                    }
                }

                if (currentStateLocal == SyncFeedState.Dormant)
                {
                    if(_logger.IsDebug) _logger.Debug($"{GetType().Name} is going to sleep.");
                    if (dormantTaskLocal == null)
                    {
                        if (_logger.IsWarn) _logger.Warn("Dormant task is NULL when trying to await it");
                    }

                    await (dormantTaskLocal?.Task ?? Task.CompletedTask);
                    if(_logger.IsDebug) _logger.Debug($"{GetType().Name} got activated.");
                }
                else if (currentStateLocal == SyncFeedState.Active)
                {
                    BlockHeader? request = await _feed.PrepareRequest(); // just to avoid null refs
                    if (request == null)
                    {
                        if (!_feed.IsMultiFeed)
                        {
                            if(_logger.IsTrace) _logger.Trace($"{_feed.GetType().Name} enqueued a null request.");
                        }

                        await Task.Delay(10, cancellationToken);
                        continue;
                    }
                    
                    SyncResponseHandlingResult result = _feed.HandleResponse(request);
                    ReactToHandlingResult(request, result, null);
                }
                else if (currentStateLocal == SyncFeedState.Finished)
                {
                    if(_logger.IsInfo) _logger.Info($"{GetType().Name} has finished work.");
                    break;
                }
            }
        }
    
    private void ReactToHandlingResult(BlockHeader request, SyncResponseHandlingResult result, PeerInfo? peer)
    {
        if (peer != null)
        {
            switch (result)
            {
                case SyncResponseHandlingResult.Emptish:
                    break;
                case SyncResponseHandlingResult.Ignored:
                    break;
                case SyncResponseHandlingResult.LesserQuality:
                    break;
                case SyncResponseHandlingResult.NoProgress:
                    break;
                case SyncResponseHandlingResult.NotAssigned:
                    break;
                case SyncResponseHandlingResult.InternalError:
                    _logger.Error($"Feed {_feed} has reported an internal error when handling {request}");
                    break;
                case SyncResponseHandlingResult.OK:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result, null);
            }
        }
    }
    
    private void SyncFeedOnStateChanged(object? sender, SyncFeedStateEventArgs e)
    {
        SyncFeedState state = e.NewState;
        UpdateState(state);
    }
    
    private void UpdateState(SyncFeedState state)
    {
        lock (_feedStateManipulation)
        {
            if (_currentFeedState != state)
            {
                if(_logger.IsDebug) _logger.Debug($"{_feed.GetType().Name} state changed to {state}");

                _currentFeedState = state;
                TaskCompletionSource<object?>? newDormantStateTask = null;
                if (state == SyncFeedState.Dormant)
                {
                    newDormantStateTask = new TaskCompletionSource<object?>();
                }

                var previous = Interlocked.Exchange(ref _dormantStateTask, newDormantStateTask);
                previous?.TrySetResult(null);
            }
        }
    }
}
