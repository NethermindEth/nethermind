// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.RegularExpressions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Logging;
using Nethermind.Network.Config;
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
        private readonly IForkInfo _forkInfo;
        private readonly ILogger _logger;
        private readonly Regex? _clientIdPattern;
        private readonly IPeerManager _peerManager;

        public ProtocolValidator(
            INodeStatsManager nodeStatsManager,
            IBlockTree blockTree,
            IForkInfo forkInfo,
            IPeerManager peerManager,
            INetworkConfig networkConfig,
            ILogManager logManager
        )
        {
            if (networkConfig.ClientIdMatcher is not null)
            {
                _clientIdPattern = new Regex(networkConfig.ClientIdMatcher, RegexOptions.Compiled);
            }
            _logger = logManager.GetClassLogger<ProtocolValidator>();
            _nodeStatsManager = nodeStatsManager;
            _blockTree = blockTree;
            _forkInfo = forkInfo;
            _peerManager = peerManager;
        }

        public bool DisconnectOnInvalid(string protocol, ISession session, ProtocolInitializedEventArgs eventArgs)
        {
            return protocol switch
            {
                Protocol.P2P => ValidateP2PProtocol(session, eventArgs),
                Protocol.Eth => ValidateEthProtocol(session, eventArgs),
                _ => true,
            };
        }

        private bool ValidateP2PProtocol(ISession session, ProtocolInitializedEventArgs eventArgs)
        {
            P2PProtocolInitializedEventArgs args = (P2PProtocolInitializedEventArgs)eventArgs;
            bool valid = ValidateP2PVersion(args.P2PVersion) || Disconnect(session, DisconnectReason.IncompatibleP2PVersion, CompatibilityValidationType.P2PVersion, $"p2p.{args.P2PVersion}");
            if (!valid) return false;

            if (_clientIdPattern?.IsMatch(args.ClientId) == false)
            {
                session.InitiateDisconnect(DisconnectReason.ClientFiltered, $"clientId: {args.ClientId}");
                return false;
            }

            if (_peerManager.ActivePeersCount > _peerManager.MaxActivePeers)
            {
                session.InitiateDisconnect(DisconnectReason.TooManyPeers, $"Too many peer");
                return false;
            }

            return true;
        }

        private bool ValidateEthProtocol(ISession session, ProtocolInitializedEventArgs eventArgs)
        {
            SyncPeerProtocolInitializedEventArgs syncPeerArgs = (SyncPeerProtocolInitializedEventArgs)eventArgs;
            if (!ValidateNetworkId(syncPeerArgs.NetworkId))
            {
                return Disconnect(session, DisconnectReason.InvalidNetworkId, CompatibilityValidationType.NetworkId, $"invalid network id - {syncPeerArgs.NetworkId}",
                    _logger.IsTrace ? $", different networkId: {BlockchainIds.GetBlockchainName(syncPeerArgs.NetworkId)}, our networkId: {BlockchainIds.GetBlockchainName(_blockTree.NetworkId)}" : "");
            }

            if (syncPeerArgs.GenesisHash != _blockTree.Genesis.Hash)
            {
                return Disconnect(session, DisconnectReason.InvalidGenesis, CompatibilityValidationType.DifferentGenesis, "invalid genesis",
                    _logger.IsTrace ? $", different genesis hash: {syncPeerArgs.GenesisHash}, our: {_blockTree.Genesis.Hash}" : "");
            }

            if (syncPeerArgs.ForkId is null)
            {
                return Disconnect(session, DisconnectReason.MissingForkId, CompatibilityValidationType.MissingForkId, "missing fork id");
            }

            ValidationResult validationResult = _forkInfo.ValidateForkId(syncPeerArgs.ForkId.Value, _blockTree.Head?.Header);
            if (validationResult != ValidationResult.Valid)
            {
                return Disconnect(session, DisconnectReason.InvalidForkId, CompatibilityValidationType.InvalidForkId, $"{validationResult}, network id {syncPeerArgs.NetworkId} fork id {syncPeerArgs.ForkId.Value}");
            }

            return true;
        }

        private bool Disconnect(ISession session, DisconnectReason reason, CompatibilityValidationType type, string details, string traceDetails = "")
        {
            if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, {details}{traceDetails}");
            _nodeStatsManager.ReportFailedValidation(session.Node, type);
            session.InitiateDisconnect(reason, details);
            if (session.Node.IsStatic && _logger.IsWarn) _logger.Warn($"Disconnected an invalid static node: {session.Node.Host}:{session.Node.Port}, reason: {reason} ({details}).");
            return false;
        }

        private static bool ValidateP2PVersion(byte p2PVersion) => p2PVersion is 4 or 5;

        private bool ValidateNetworkId(ulong networkId) => networkId == _blockTree.NetworkId;
    }
}
