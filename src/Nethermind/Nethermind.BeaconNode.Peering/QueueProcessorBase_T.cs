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
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Peering
{
    public abstract class QueueProcessorBase<T> : BackgroundService
    {
        protected ILogger _logger;

        public QueueProcessorBase(ILogger logger)
        {
            _logger = logger;
            Queue = new BlockingCollection<T>(MaximumQueue);
        }

        protected virtual int MaximumQueue { get; } = 1024;

        protected BlockingCollection<T> Queue { get; }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Queue?.CompleteAdding();
                Queue?.Dispose();
            }
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Don't want BackgroundService.StartAsync to get stuck waiting for queue, so force continuation
                await Task.Delay(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
                
                if (_logger.IsInfo())
                    Log.QueueProcessorExecuteStarting(_logger, GetType().Name, null);

                foreach (T item in Queue.GetConsumingEnumerable())
                {
                    await ProcessItemAsync(item).ConfigureAwait(false);
                    if (stoppingToken.IsCancellationRequested)
                    {
                        Queue.CompleteAdding();
                        break;
                    }
                }
            }
            catch
            {
                try
                {
                    Queue.CompleteAdding();
                }
                catch
                {
                }
            }
        }

        protected abstract Task ProcessItemAsync(T item);

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            Queue?.CompleteAdding();
            return base.StopAsync(cancellationToken);
        }
    }
}