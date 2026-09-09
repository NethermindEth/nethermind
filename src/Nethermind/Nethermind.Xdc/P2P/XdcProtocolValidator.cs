// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Stats;

namespace Nethermind.Xdc.P2P;

internal class XdcProtocolValidator(
    INodeStatsManager nodeStatsManager,
    IBlockTree blockTree,
    IForkInfo forkInfo,
    INetworkConfig networkConfig,
    ILogManager logManager) : ProtocolValidator(nodeStatsManager, blockTree, forkInfo, networkConfig, logManager)
{
    /// <remarks>The legacy XDPoS 2.0 handshake has no fork ID field, so it cannot be validated.</remarks>
    protected override bool MustValidateForkId(byte protocolVersion) =>
        protocolVersion >= XdcProtocolVersions.FirstVersionWithForkId;
}
