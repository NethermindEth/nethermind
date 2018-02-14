using System;

namespace Nevermind.Network.P2P
{
    public class P2PMessageFactory : IMessageFactory<P2PMessage>
    {
        private readonly IMessageSerializationService _serializationService;

        public P2PMessageFactory(IMessageSerializationService serializationService)
        {
            _serializationService = serializationService;
        }

        public P2PMessage Create(int protocolType, int packetType, byte[] serializedData)
        {
            // TODO: continue below..
            if (packetType == MessageCode.Ping)
            {
                _serializationService.Deserialize<PingMessage>(serializedData);
            }

            throw new NotImplementedException();
        }
    }
}