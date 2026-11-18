// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.FastSync
{
    [DebuggerDisplay("Requested Nodes: {RequestedNodes?.Count ?? 0}, Responses: {Responses?.Count ?? 0}, Assigned: {AssignedPeer?.Current}")]
    public class StateSyncBatch(Hash256 stateRoot, NodeDataType nodeDataType, IList<StateSyncItem> requestedNodes) : IDisposable
    {
        public NodeDataType NodeDataType { get; } = nodeDataType;

        public Hash256 StateRoot = stateRoot;

        public IList<StateSyncItem>? RequestedNodes { get; } = requestedNodes;

        private IByteArrayList? _responses;
        public IByteArrayList? Responses
        {
            get => Volatile.Read(ref _responses);
            set => Volatile.Write(ref _responses, value);
        }

        internal long ResponseMilliseconds { get; set; }
        internal long ValidatedBytes { get; set; }
        internal bool ResponseInspected { get; set; }

        public int ConsumerId { get; set; }

        public override string ToString() => $"{RequestedNodes?.Count ?? 0} state sync requests with {Responses?.Count ?? 0} responses";

        public void Dispose() => Interlocked.Exchange(ref _responses, null)?.Dispose();
    }
}
