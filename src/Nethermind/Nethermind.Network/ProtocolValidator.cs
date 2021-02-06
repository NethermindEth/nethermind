//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    [Todo(Improve.Refactor, "Allow protocols validators to be loaded per protocol")]
    public class ProtocolValidator : IProtocolValidator
    {
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly IBlockTree _blockTree;
        private ILogger _logger;

        public ProtocolValidator(INodeStatsManager nodeStatsManager, IBlockTree blockTree, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<ProtocolValidator>() ?? throw new ArgumentNullException(nameof(logManager));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public bool DisconnectOnInvalid(string protocol, ISession session, ProtocolInitializedEventArgs eventArgs)
        {
            switch (protocol)
            {
                case Protocol.P2P:
                    P2PProtocolInitializedEventArgs args = (P2PProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateP2PVersion(args.P2PVersion))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, incorrect P2PVersion: {args.P2PVersion}");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.P2PVersion);
                        Disconnect(session, DisconnectReason.IncompatibleP2PVersion, $"p2p.{args.P2PVersion}");
                        if (session.Node.IsStatic && _logger.IsWarn) _logger.Warn($"Disconnected an invalid static node: {session.Node.Host}:{session.Node.Port}, reason: {DisconnectReason.IncompatibleP2PVersion}");
                        return false;
                    }

                    if (!ValidateCapabilities(args.Capabilities))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, no Eth62 capability, supported capabilities: [{string.Join(",", args.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"))}]");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.Capabilities);
                        Disconnect(session, DisconnectReason.UselessPeer, "capabilities");
                        if (session.Node.IsStatic && _logger.IsWarn) _logger.Warn($"Disconnected an invalid static node: {session.Node.Host}:{session.Node.Port}, reason: {DisconnectReason.UselessPeer} (capabilities)");
                        return false;
                    }

                    break;
                case Protocol.Eth:
                case Protocol.Les:
                    SyncPeerProtocolInitializedEventArgs syncPeerArgs = (SyncPeerProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateChainId(syncPeerArgs.ChainId))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different chainId: {ChainId.GetChainName(syncPeerArgs.ChainId)}, our chainId: {ChainId.GetChainName(_blockTree.ChainId)}");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.ChainId);
                        Disconnect(session, DisconnectReason.UselessPeer, $"invalid chain id - {syncPeerArgs.ChainId}");
                        if (session.Node.IsStatic && _logger.IsWarn) _logger.Warn($"Disconnected an invalid static node: {session.Node.Host}:{session.Node.Port}, reason: {DisconnectReason.UselessPeer} (invalid chain id - {syncPeerArgs.ChainId})");
                        return false;
                    }

                    if (syncPeerArgs.GenesisHash != _blockTree.Genesis.Hash)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different genesis hash: {syncPeerArgs.GenesisHash}, our: {_blockTree.Genesis.Hash}");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.DifferentGenesis);
                        Disconnect(session, DisconnectReason.BreachOfProtocol, "invalid genesis");
                        if (session.Node.IsStatic && _logger.IsWarn) _logger.Warn($"Disconnected an invalid static node: {session.Node.Host}:{session.Node.Port}, reason: {DisconnectReason.BreachOfProtocol} (invalid genesis)");
                        return false;
                    }

                    break;
            }

            return true;
        }

        private void Disconnect(ISession session, DisconnectReason reason, string details)
        {
            session.InitiateDisconnect(reason, details);
        }

        private bool ValidateP2PVersion(byte p2PVersion)
        {
            return p2PVersion == 4 || p2PVersion == 5;
        }

        private bool ValidateCapabilities(IEnumerable<Capability> capabilities)
        {
            // TODO: this is duplicated from P2PProtocolHandler.HandleHello. One should probably be removed
            return capabilities.Any(x =>
                // x.ProtocolCode == Protocol.Les ||
                x.ProtocolCode == Protocol.Eth && (
                    x.Version == 62 || 
                    x.Version == 63 || 
                    x.Version == 64 || 
                    x.Version == 65));
        }

        private bool ValidateChainId(ulong chainId)
        {
            return chainId == _blockTree.ChainId;
        }
    }
}
