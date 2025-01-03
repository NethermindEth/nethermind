using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Stats;

namespace Nethermind.Network.P2P.Subprotocols.Etha
{
    public class EthaProtocolFactory : ProtocolFactoryBase
    {
        private readonly IBlockTree _blockTree;
        private readonly IMessageSerializationService _serializationService;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly ILogManager _logManager;

        public EthaProtocolFactory(
            IBlockTree blockTree,
            IMessageSerializationService serializationService,
            INodeStatsManager nodeStatsManager,
            ILogManager logManager)
        {
            _blockTree = blockTree;
            _serializationService = serializationService;
            _nodeStatsManager = nodeStatsManager;
            _logManager = logManager;
        }

        public override Protocol Create(ISession session)
        {
            var protocol = new EthaProtocol();
            var protocolHandler = new EthaProtocolHandler(
                _blockTree,
                _serializationService,
                _nodeStatsManager,
                _logManager);

            protocol.InitializeProtocolHandler(protocolHandler);

            return protocol;
        }

        public override string Name => "etha";
    }
}
