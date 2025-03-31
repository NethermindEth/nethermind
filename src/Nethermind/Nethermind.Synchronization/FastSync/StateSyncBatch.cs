// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.FastSync
{
    [DebuggerDisplay("Requested Nodes: {RequestedNodes?.Count ?? 0}, Responses: {Responses?.Length ?? 0}, Assigned: {AssignedPeer?.Current}")]
    public class StateSyncBatch(Hash256 stateRoot, NodeDataType nodeDataType, IList<StateSyncItem> requestedNodes) : IDisposable
    {
        public NodeDataType NodeDataType { get; } = nodeDataType;

        public Hash256 StateRoot = stateRoot;

        public IList<StateSyncItem>? RequestedNodes { get; } = requestedNodes;

        public IOwnedReadOnlyList<byte[]>? Responses { get; set; }

        public int ConsumerId { get; set; }

        public override string ToString()
        {
            return $"{RequestedNodes?.Count ?? 0} state sync requests with {Responses?.Count ?? 0} responses";
        }

        public void Dispose()
        {
            Responses?.Dispose();
        }
    }
}
