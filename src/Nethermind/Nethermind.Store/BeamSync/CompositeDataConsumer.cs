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
using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.Store.BeamSync
{
    public class CompositeDataConsumer : INodeDataConsumer
    {
        private readonly INodeDataConsumer[] _consumers;

        public CompositeDataConsumer(params INodeDataConsumer[] consumers)
        {
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

        public event EventHandler NeedMoreData;

        public Keccak[] PrepareRequest()
        {
            foreach (INodeDataConsumer nodeDataConsumer in _consumers)
            {
                if (nodeDataConsumer.NeedsData)
                {
                    return nodeDataConsumer.PrepareRequest();
                }
            }

            // throw new InvalidOperationException("No data needed");
            return Array.Empty<Keccak>();
        }

        public int HandleResponse(Keccak[] hashes, byte[][] data)
        {
            int consumed = 0;
            foreach (INodeDataConsumer nodeDataConsumer in _consumers)
            {
                consumed += nodeDataConsumer.HandleResponse(hashes, data);
            }

            return consumed;
        }

        public bool NeedsData => _consumers.Any(c => c.NeedsData);
    }
}