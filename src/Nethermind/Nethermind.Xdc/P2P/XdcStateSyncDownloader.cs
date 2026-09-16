// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.Synchronization.StateSync;

namespace Nethermind.Xdc.P2P;

internal class XdcStateSyncDownloader(ILogManager logManager) : StateSyncDownloader(logManager)
{
    /// <remarks>
    /// XDC keeps <c>GetNodeData</c> on every version - it never applied the eth/67 removal - but its versions
    /// sit above the eth/67 cut-off the base class checks against.
    /// </remarks>
    protected override bool ProtocolSupportsNodeData(ISyncPeer peer) =>
        XdcProtocolVersions.IsXdcVersion(peer.ProtocolVersion) || base.ProtocolSupportsNodeData(peer);
}
