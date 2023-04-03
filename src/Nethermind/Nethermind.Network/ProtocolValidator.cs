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
        private readonly ForkInfo _forkInfo;
        private ILogger _logger;

        public ProtocolValidator(INodeStatsManager nodeStatsManager, IBlockTree blockTree, ForkInfo forkInfo, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<ProtocolValidator>() ?? throw new ArgumentNullException(nameof(logManager));
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _forkInfo = forkInfo ?? throw new ArgumentNullException(nameof(forkInfo));
        }

        public bool DisconnectOnInvalid(string protocol, ISession session, ProtocolInitializedEventArgs eventArgs)
        {
            return protocol switch
            {
                Protocol.P2P => ValidateP2PProtocol(session, eventArgs),
                Protocol.Eth or Protocol.Les => ValidateEthProtocol(session, eventArgs),
                _ => true,
            };
        }

        private bool ValidateP2PProtocol(ISession session, ProtocolInitializedEventArgs eventArgs)
        {
            P2PProtocolInitializedEventArgs args = (P2PProtocolInitializedEventArgs)eventArgs;
            return ValidateP2PVersion(args.P2PVersion) || Disconnect(session, InitiateDisconnectReason.IncompatibleP2PVersion, CompatibilityValidationType.P2PVersion, $"p2p.{args.P2PVersion}");
        }

        private bool ValidateEthProtocol(ISession session, ProtocolInitializedEventArgs eventArgs)
        {
            SyncPeerProtocolInitializedEventArgs syncPeerArgs = (SyncPeerProtocolInitializedEventArgs)eventArgs;
            if (!ValidateNetworkId(syncPeerArgs.NetworkId))
            {
                return Disconnect(session, InitiateDisconnectReason.InvalidNetworkId, CompatibilityValidationType.NetworkId, $"invalid network id - {syncPeerArgs.NetworkId}",
                    _logger.IsTrace ? $", different networkId: {BlockchainIds.GetBlockchainName(syncPeerArgs.NetworkId)}, our networkId: {BlockchainIds.GetBlockchainName(_blockTree.NetworkId)}" : "");
            }

            if (syncPeerArgs.GenesisHash != _blockTree.Genesis.Hash)
            {
                return Disconnect(session, InitiateDisconnectReason.InvalidGenesis, CompatibilityValidationType.DifferentGenesis, "invalid genesis",
                    _logger.IsTrace ? $", different genesis hash: {syncPeerArgs.GenesisHash}, our: {_blockTree.Genesis.Hash}" : "");
            }

            if (syncPeerArgs.ForkId == null)
            {
                return Disconnect(session, InitiateDisconnectReason.MissingForkId, CompatibilityValidationType.MissingForkId, "missing fork id");
            }

            if (_forkInfo.ValidateForkId(syncPeerArgs.ForkId.Value, _blockTree.Head?.Header) != ValidationResult.Valid)
            {
                return Disconnect(session, InitiateDisconnectReason.InvalidForkId, CompatibilityValidationType.InvalidForkId, "invalid fork id");
            }

            return true;
        }

        private bool Disconnect(ISession session, InitiateDisconnectReason reason, CompatibilityValidationType type, string details, string traceDetails = "")
        {
            if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, {details}{traceDetails}");
            _nodeStatsManager.ReportFailedValidation(session.Node, type);
            session.InitiateDisconnect(reason, details);
            if (session.Node.IsStatic && _logger.IsWarn) _logger.Warn($"Disconnected an invalid static node: {session.Node.Host}:{session.Node.Port}, reason: {reason} ({details}).");
            return false;
        }

        private bool ValidateP2PVersion(byte p2PVersion) => p2PVersion is 4 or 5;

        private bool ValidateNetworkId(ulong networkId) => networkId == _blockTree.NetworkId;
    }
}
