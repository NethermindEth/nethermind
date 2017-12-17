namespace Nevermind.Network
{
    public class PongMessage : P2PMessage
    {
        public static PongMessage Instance = new PongMessage();

        private PongMessage()
        {
        }

        public override int MessageId => MessageCode.Pong;
    }
}