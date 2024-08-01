// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;

namespace Nethermind.Synchronization
{
    public class SyncEventArgs : EventArgs
    {
        public ISyncPeer Peer { get; }
        public SyncEvent SyncEvent { get; }

        public SyncEventArgs(ISyncPeer peer, SyncEvent @event)
        {
            Peer = peer;
            SyncEvent = @event;
        }
    }
}
