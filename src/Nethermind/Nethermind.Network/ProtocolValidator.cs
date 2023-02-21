// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
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
                    P2PProtocolInitializedEventArgs args = (P2PProtocolInitializedEventArgs)eventArgs;
                    if (!ValidateP2PVersion(args.P2PVersion))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, incorrect P2PVersion: {args.P2PVersion}");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.P2PVersion);
                        Disconnect(session, InitiateDisconnectReason.IncompatibleP2PVersion, $"p2p.{args.P2PVersion}");
                        if (session.Node.IsStatic && _logger.IsWarn) _logger.Warn($"Disconnected an invalid static node: {session.Node.Host}:{session.Node.Port}, reason: {DisconnectReason.IncompatibleP2PVersion}");
                        return false;
                    }

                    break;
                case Protocol.Eth:
                case Protocol.Les:
                    SyncPeerProtocolInitializedEventArgs syncPeerArgs = (SyncPeerProtocolInitializedEventArgs)eventArgs;
                    if (!ValidateNetworkId(syncPeerArgs.NetworkId))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different network id: {BlockchainIds.GetBlockchainName(syncPeerArgs.NetworkId)}, our network id: {BlockchainIds.GetBlockchainName(_blockTree.NetworkId)}");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.NetworkId);
                        Disconnect(session, InitiateDisconnectReason.InvalidChainId, $"invalid network id - {syncPeerArgs.NetworkId}");
                        if (session.Node.IsStatic && _logger.IsWarn) _logger.Warn($"Disconnected an invalid static node: {session.Node.Host}:{session.Node.Port}, reason: {DisconnectReason.UselessPeer} (invalid network id - {syncPeerArgs.NetworkId})");
                        return false;
                    }

                    if (syncPeerArgs.GenesisHash != _blockTree.Genesis.Hash)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different genesis hash: {syncPeerArgs.GenesisHash}, our: {_blockTree.Genesis.Hash}");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.DifferentGenesis);
                        Disconnect(session, InitiateDisconnectReason.InvalidGenesis, "invalid genesis");
                        if (session.Node.IsStatic && _logger.IsWarn) _logger.Warn($"Disconnected an invalid static node: {session.Node.Host}:{session.Node.Port}, reason: {DisconnectReason.BreachOfProtocol} (invalid genesis)");
                        return false;
                    }

                    break;
            }

            return true;
        }

        private void Disconnect(ISession session, InitiateDisconnectReason reason, string details)
        {
            session.InitiateDisconnect(reason, details);
        }

        private bool ValidateP2PVersion(byte p2PVersion)
        {
            return p2PVersion == 4 || p2PVersion == 5;
        }

        private bool ValidateNetworkId(ulong networkId)
        {
            return networkId == _blockTree.NetworkId;
        }
    }
}
