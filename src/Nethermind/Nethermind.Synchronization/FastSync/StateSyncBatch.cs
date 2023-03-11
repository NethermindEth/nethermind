// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.FastSync
{
    [DebuggerDisplay("Requested Nodes: {RequestedNodes?.Count ?? 0}, Responses: {Responses?.Length ?? 0}, Assigned: {AssignedPeer?.Current}")]
    public class StateSyncBatch
    {
        public StateSyncBatch(Keccak stateRoot, NodeDataType nodeDataType, IList<StateSyncItem> requestedNodes)
        {
            StateRoot = stateRoot;
            NodeDataType = nodeDataType;
            RequestedNodes = requestedNodes;
        }

        public NodeDataType NodeDataType { get; }

        public Keccak StateRoot;

        public IList<StateSyncItem>? RequestedNodes { get; }

        public byte[][]? Responses { get; set; }

        public int ConsumerId { get; set; }

        public override string ToString()
        {
            return $"{RequestedNodes?.Count ?? 0} state sync requests with {Responses?.Length ?? 0} responses";
        }
    }
}
