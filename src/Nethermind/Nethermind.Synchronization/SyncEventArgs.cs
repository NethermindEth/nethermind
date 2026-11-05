// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;

namespace Nethermind.Synchronization
{
    public class SyncEventArgs(ISyncPeer peer, SyncEvent @event) : EventArgs
    {
        public ISyncPeer Peer { get; } = peer;
        public SyncEvent SyncEvent { get; } = @event;
    }
}
