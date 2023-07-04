// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;

namespace Nethermind.Synchronization.Peers
{
    public class PeerBlockNotificationEventArgs : EventArgs
    {
        public ISyncPeer SyncPeer { get; }
        public Block Block { get; }

        public PeerBlockNotificationEventArgs(ISyncPeer syncPeer, Block block)
        {
            SyncPeer = syncPeer;
            Block = block;
        }
    }
}
