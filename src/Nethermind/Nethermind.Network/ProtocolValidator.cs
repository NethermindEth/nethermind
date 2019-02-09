using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    [Todo(Improve.Refactor, "Allo protocols validators to be loaded per protocol")]
    public class ProtocolValidator : IProtocolValidator
    {
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly int _chainId;
        private readonly Keccak _genesisHash;
        private ILogger _logger;

        public ProtocolValidator(INodeStatsManager nodeStatsManager, int chainId, Keccak genesisHash, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger();
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _chainId = chainId;
            _genesisHash = genesisHash;
        }

        public bool DisconnectOnInvalid(string protocol, IP2PSession session, ProtocolInitializedEventArgs eventArgs)
        {
            switch (protocol)
            {
                case Protocol.P2P:
                    var args = (P2PProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateP2PVersion(args.P2PVersion))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, incorrect P2PVersion: {args.P2PVersion}");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.P2PVersion);
                        Disconnect(protocol, session, DisconnectReason.IncompatibleP2PVersion);
                        return false;
                    }

                    if (!ValidateCapabilities(args.Capabilities))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, no Eth62 capability, supported capabilities: [{string.Join(",", args.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"))}]");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.Capabilities);
                        Disconnect(protocol, session, DisconnectReason.UselessPeer);
                        return false;
                    }

                    break;
                case Protocol.Eth:
                    var ethArgs = (EthProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateChainId(ethArgs.ChainId))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different chainId: {ChainId.GetChainName((int) ethArgs.ChainId)}, our chainId: {ChainId.GetChainName(_chainId)}");

                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.ChainId);
                        Disconnect(protocol, session, DisconnectReason.UselessPeer);
                        return false;
                    }

                    if (ethArgs.GenesisHash != _genesisHash)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different genesis hash: {ethArgs.GenesisHash}, our: {_genesisHash}");

                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.DifferentGenesis);
                        Disconnect(protocol, session, DisconnectReason.BreachOfProtocol);
                        return false;
                    }

                    break;
            }

            return true;
        }

        private void Disconnect(string protocol, IP2PSession session, DisconnectReason reason)
        {
            session.InitiateDisconnectAsync(reason).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Failed to disconnect invalid protocol {protocol} for {session.Node.Id}");
                    }
                }
            );
        }

        private bool ValidateP2PVersion(byte p2PVersion)
        {
            return p2PVersion == 4 || p2PVersion == 5;
        }

        private bool ValidateCapabilities(IEnumerable<Capability> capabilities)
        {
            return capabilities.Any(x => x.ProtocolCode == Protocol.Eth && (x.Version == 62 || x.Version == 63));
        }

        private bool ValidateChainId(long chainId)
        {
            return chainId == _chainId;
        }
    }
}