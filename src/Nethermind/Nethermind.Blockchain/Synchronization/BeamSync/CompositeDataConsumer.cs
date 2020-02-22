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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Blockchain.Synchronization.BeamSync
{
    public class CompositeDataConsumer : INodeDataConsumer
    {
        private readonly INodeDataConsumer[] _consumers;
        private ILogger _logger;

        public CompositeDataConsumer(ILogManager logManager, params INodeDataConsumer[] consumers)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _consumers = consumers;
            foreach (INodeDataConsumer dataConsumer in _consumers)
            {
                dataConsumer.NeedMoreData += DataConsumerOnNeedMoreData;
            }
        }

        private void DataConsumerOnNeedMoreData(object sender, EventArgs e)
        {
            NeedMoreData?.Invoke(this, EventArgs.Empty);
        }

        public UInt256 RequiredPeerDifficulty => _consumers.Max(c => c.RequiredPeerDifficulty);
        public event EventHandler NeedMoreData;

        public DataConsumerRequest[] PrepareRequests()
        {
            List<DataConsumerRequest> combined = new List<DataConsumerRequest>();
            foreach (INodeDataConsumer nodeDataConsumer in _consumers)
            {
                DataConsumerRequest[] requests = nodeDataConsumer.PrepareRequests();
                combined.AddRange(requests);
            }

            if (combined.Count > 0)
            {
                // if (_logger.IsInfo) _logger.Info($"Prepared a combined request of length {combined.Count}");
                return combined.ToArray();
            }

            return Array.Empty<DataConsumerRequest>();
        }

        public int HandleResponse(DataConsumerRequest request, byte[][] data)
        {
            int consumed = 0;
            foreach (INodeDataConsumer nodeDataConsumer in _consumers)
            {
                consumed += nodeDataConsumer.HandleResponse(request, data);
            }

            return consumed;
        }
    }
}