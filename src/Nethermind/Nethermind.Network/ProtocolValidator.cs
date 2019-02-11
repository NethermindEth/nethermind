using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
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
        private readonly IBlockTree _blockTree;
        private ILogger _logger;

        public ProtocolValidator(INodeStatsManager nodeStatsManager, IBlockTree blockTree, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger();
            _nodeStatsManager = nodeStatsManager ?? throw new ArgumentNullException(nameof(nodeStatsManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }

        public bool DisconnectOnInvalid(string protocol, ISession session, ProtocolInitializedEventArgs eventArgs)
        {
            switch (protocol)
            {
                case Protocol.P2P:
                    var args = (P2PProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateP2PVersion(args.P2PVersion))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, incorrect P2PVersion: {args.P2PVersion}");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.P2PVersion);
                        Disconnect(session, DisconnectReason.IncompatibleP2PVersion);
                        return false;
                    }

                    if (!ValidateCapabilities(args.Capabilities))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, no Eth62 capability, supported capabilities: [{string.Join(",", args.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"))}]");
                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.Capabilities);
                        Disconnect(session, DisconnectReason.UselessPeer);
                        return false;
                    }

                    break;
                case Protocol.Eth:
                    var ethArgs = (EthProtocolInitializedEventArgs) eventArgs;
                    if (!ValidateChainId(ethArgs.ChainId))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different chainId: {ChainId.GetChainName((int) ethArgs.ChainId)}, our chainId: {ChainId.GetChainName(_blockTree.ChainId)}");

                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.ChainId);
                        Disconnect(session, DisconnectReason.UselessPeer);
                        return false;
                    }

                    if (ethArgs.GenesisHash != _blockTree.Genesis.Hash)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Initiating disconnect with peer: {session.RemoteNodeId}, different genesis hash: {ethArgs.GenesisHash}, our: {_blockTree.Genesis.Hash}");

                        _nodeStatsManager.ReportFailedValidation(session.Node, CompatibilityValidationType.DifferentGenesis);
                        Disconnect(session, DisconnectReason.BreachOfProtocol);
                        return false;
                    }

                    break;
            }

            return true;
        }

        private void Disconnect(ISession session, DisconnectReason reason)
        {
            session.InitiateDisconnect(reason);
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
            return chainId == _blockTree.ChainId;
        }
    }
}