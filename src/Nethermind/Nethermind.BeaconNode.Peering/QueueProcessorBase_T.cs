// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
