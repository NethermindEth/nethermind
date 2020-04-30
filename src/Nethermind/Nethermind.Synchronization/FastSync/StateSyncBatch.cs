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

using System.Collections.Generic;
using System.Diagnostics;

namespace Nethermind.Synchronization.FastSync
{
    public interface IStateSyncBatch
    {
        IEnumerable<StateSyncItem> AllRequestedNodes { get; }

        byte[][] Responses { set; }
    }

    public class MultiStateSyncBatch : IStateSyncBatch
    {
        public IEnumerable<StateSyncBatch> Batches { get; }

        public MultiStateSyncBatch(IEnumerable<StateSyncBatch> batches)
        {
            Batches = batches;
        }

        public IEnumerable<StateSyncItem> AllRequestedNodes
        {
            get
            {
                foreach (StateSyncBatch stateSyncBatch in Batches)
                {
                    foreach (StateSyncItem stateSyncItem in stateSyncBatch.AllRequestedNodes)
                    {
                        yield return stateSyncItem;
                    }
                }
            }
        }

        public byte[][] Responses
        {
            set
            {
                int indexInResponse = 0;
                foreach (StateSyncBatch batch in Batches)
                {
                    batch.Responses = new byte[batch.RequestedNodes.Length][];
                    for (int i = 0; i < batch.RequestedNodes.Length; i++)
                    {
                        batch.Responses[i] = value[indexInResponse];
                        indexInResponse++;
                    }
                }
            }
        }
    }

    [DebuggerDisplay("Requested Nodes: {RequestedNodes?.Length ?? 0}, Responses: {Responses?.Length ?? 0}, Assigned: {AssignedPeer?.Current}")]
    public class StateSyncBatch : IStateSyncBatch
    {
        public StateSyncItem[] RequestedNodes { get; set; }

        public IEnumerable<StateSyncItem> AllRequestedNodes => RequestedNodes;

        public byte[][] Responses { get; set; }

        public int FeedId { get; set; }

        public override string ToString()
        {
            return $"{RequestedNodes?.Length ?? 0} state sync requests with {Responses?.Length ?? 0} responses";
        }
    }
}