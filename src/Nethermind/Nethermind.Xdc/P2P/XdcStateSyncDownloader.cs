// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Xdc;

internal class XdcStateSyncDownloader(ILogManager logManager) : StateSyncDownloader(logManager)
{
    protected override bool ProtocolSupportsNodeData(ISyncPeer peer)
    {
        return peer.ProtocolVersion < 101;
    }
}
