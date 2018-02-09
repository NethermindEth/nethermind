namespace Nevermind.Network
{
    public class P2PMessageConsumer : MessageConsumerBase<Packet>
    {
        private readonly IMessageFactory<P2PMessage> _messageFactory;

        public P2PMessageConsumer(IMessageFactory<P2PMessage> messageFactory)
        {
            _messageFactory = messageFactory;
        }

        protected override bool Consume(Packet input)
        {
            P2PMessage messageBase = _messageFactory.Create(input.ProtocolType ?? 0, input.PacketType ?? 0, input.Data); // TODO: check the 0s

            // different type of consumers? P2P, whisper...
            // TODO: consider merge factory and consumer?

            return true;
        }
    }
}