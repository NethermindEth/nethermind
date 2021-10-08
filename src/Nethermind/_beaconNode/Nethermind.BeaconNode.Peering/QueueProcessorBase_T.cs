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
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Peering
{
    public abstract class QueueProcessorBase<T> : BackgroundService
    {
        private readonly Channel<(T item, string? activityId)> _channel;
        private readonly ILogger _logger;

        protected QueueProcessorBase(ILogger logger, int maximumQueue)
        {
            _logger = logger;
            _channel = Channel.CreateBounded<(T item, string? activityId)>(new BoundedChannelOptions(maximumQueue));
        }

        protected bool EnqueueItem(T item)
        {
            string? activityId = Activity.Current?.Id;
            return _channel.Writer.TryWrite((item, activityId));
        }
        
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_logger.IsInfo())
                    Log.QueueProcessorExecuteStarting(_logger, GetType().Name, null);

                await foreach ((T item, string? parentId) in _channel.Reader.ReadAllAsync(stoppingToken)
                    .ConfigureAwait(false))
                {
                    Activity activity = new Activity("process-item");
                    if (parentId != null)
                    {
                        activity.SetParentId(parentId);
                    }

                    activity.Start();
                    try
                    {
                        await ProcessItemAsync(item).ConfigureAwait(false);
                    }
                    finally
                    {
                        activity.Stop();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stopping token; ignore
            }
            catch (Exception ex)
            {
                Log.QueueProcessorCriticalError(_logger, GetType().Name, ex);
                _channel.Writer.Complete(ex);
            }
        }

        protected abstract Task ProcessItemAsync(T item);

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _channel.Writer.Complete();
            return base.StopAsync(cancellationToken);
        }
    }
}