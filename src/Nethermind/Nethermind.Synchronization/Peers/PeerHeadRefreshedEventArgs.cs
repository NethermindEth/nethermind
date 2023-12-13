// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;

namespace Nethermind.Synchronization.Peers;

public class PeerHeadRefreshedEventArgs : EventArgs
{
    public ISyncPeer SyncPeer { get; }
    public BlockHeader Header { get; }

    public PeerHeadRefreshedEventArgs(ISyncPeer syncPeer, BlockHeader blockHeader)
    {
        SyncPeer = syncPeer;
        Header = blockHeader;
    }
}
