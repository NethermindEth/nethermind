// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;

namespace Nethermind.Synchronization.Peers
{
    public class PeerBlockNotificationEventArgs(ISyncPeer syncPeer, Block block) : EventArgs
    {
        public ISyncPeer SyncPeer { get; } = syncPeer;
        public Block Block { get; } = block;
    }
}
