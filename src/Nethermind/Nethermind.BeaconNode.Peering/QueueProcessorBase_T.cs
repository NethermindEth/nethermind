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
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Peering
{
    public abstract class QueueProcessorBase<T> : BackgroundService
    {
        protected ILogger _logger;
        private readonly Channel<T> _channel;

        protected QueueProcessorBase(ILogger logger, int maximumQueue = 1024)
        {
            _logger = logger;
            _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(maximumQueue));
        }

        protected ChannelWriter<T> ChannelWriter => _channel.Writer;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                if (_logger.IsInfo())
                    Log.QueueProcessorExecuteStarting(_logger, GetType().Name, null);

                await foreach (T item in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(true))
                {
                    await ProcessItemAsync(item).ConfigureAwait(false);
                }
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