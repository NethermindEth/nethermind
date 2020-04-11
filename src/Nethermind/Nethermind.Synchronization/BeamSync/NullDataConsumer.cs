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
using Nethermind.Dirichlet.Numerics;
using Nethermind.Synchronization.TotalSync;

namespace Nethermind.Synchronization.BeamSync
{
    public class NullDataConsumer : INodeDataConsumer
    {
        private static NullDataConsumer _instance = new NullDataConsumer();

        private NullDataConsumer()
        {
        }

        public static NullDataConsumer Instance => LazyInitializer.EnsureInitialized(ref _instance);

        public UInt256 RequiredPeerDifficulty => UInt256.Zero;

        public event EventHandler NeedMoreData
        {
            add { }
            remove { }
        }

        public DataConsumerRequest[] PrepareRequests()
        {
            return Array.Empty<DataConsumerRequest>();
        }

        public SyncResponseHandlingResult HandleResponse(DataConsumerRequest request, byte[][] data)
        {
            throw new InvalidOperationException("Should never receive response here");
        }

        public bool NeedsData => false;
    }
}