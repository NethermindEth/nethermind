using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Synchronization;

namespace Nethermind.Network.P2P.Subprotocols.Etha
{
    /// <summary>
    /// Factory for creating instances of the Etha protocol and its handlers.
    /// </summary>
    public class EthaProtocolFactory : ProtocolFactoryBase
    {
        private readonly IBlockTree _blockTree;
        private readonly IMessageSerializationService _serializationService;
        private readonly INodeStatsManager _nodeStatsManager;
        private readonly ISyncServer _syncServer;
        private readonly ILogManager _logManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="EthaProtocolFactory"/> class.
        /// </summary>
        /// <param name="blockTree">The block tree for managing blockchain data.</param>
        /// <param name="serializationService">The message serialization service.</param>
        /// <param name="nodeStatsManager">The node statistics manager.</param>
        /// <param name="syncServer">The synchronization server.</param>
        /// <param name="logManager">The log manager.</param>
        public EthaProtocolFactory(
            IBlockTree blockTree,
            IMessageSerializationService serializationService,
            INodeStatsManager nodeStatsManager,
            ISyncServer syncServer,
            ILogManager logManager)
        {
            _blockTree = blockTree;
            _serializationService = serializationService;
            _nodeStatsManager = nodeStatsManager;
            _syncServer = syncServer;
            _logManager = logManager;
        }

        /// <summary>
        /// Creates a new instance of the Etha protocol with its handler.
        /// </summary>
        /// <param name="session">The network session for the protocol.</param>
        /// <returns>A new instance of the Etha protocol.</returns>
        public override Protocol Create(ISession session)
        {
            var protocol = new EthaProtocol();
            var protocolHandler = new EthaProtocolHandler(
                session,
                _blockTree,
                _serializationService,
                _nodeStatsManager,
                _syncServer,
                _logManager);

            protocol.InitializeProtocolHandler(protocolHandler);

            return protocol;
        }

        /// <summary>
        /// Gets the name of the protocol factory.
        /// </summary>
        public override string Name => "etha";
    }
} 
