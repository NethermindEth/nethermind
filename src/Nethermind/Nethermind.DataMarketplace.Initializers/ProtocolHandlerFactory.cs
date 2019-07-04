using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.DataMarketplace.Subprotocols;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;

namespace Nethermind.DataMarketplace.Initializers
{
    public class ProtocolHandlerFactory : IProtocolHandlerFactory
    {
        private readonly INdmSubprotocolFactory _subprotocolFactory;
        private readonly IProtocolValidator _protocolValidator;
        private readonly IEthRequestService _ethRequestService;
        private readonly ILogger _logger;

        public ProtocolHandlerFactory(INdmSubprotocolFactory subprotocolFactory, IProtocolValidator protocolValidator,
            IEthRequestService ethRequestService, ILogManager logManager)
        {
            _subprotocolFactory = subprotocolFactory;
            _protocolValidator = protocolValidator;
            _ethRequestService = ethRequestService;
            _logger = logManager.GetClassLogger();
        }
        
        public IProtocolHandler Create(ISession session)
        {
            var handler = _subprotocolFactory.Create(session);
            handler.ProtocolInitialized += (sender, args) =>
            {
                var ndmEventArgs = (NdmProtocolInitializedEventArgs) args;

                _protocolValidator.DisconnectOnInvalid(Protocol.Ndm, session, ndmEventArgs);
                if (_logger.IsTrace) _logger.Trace($"NDM version {handler.ProtocolVersion}: {session.RemoteNodeId}, host: {session.Node.Host}");
                if (string.IsNullOrWhiteSpace(_ethRequestService.FaucetHost) ||
                    !session.Node.Host.Contains(_ethRequestService.FaucetHost))
                {
                    return;
                }

                _ethRequestService.UpdateFaucet(handler as INdmPeer);
            };

            return handler;
        }
    }
}