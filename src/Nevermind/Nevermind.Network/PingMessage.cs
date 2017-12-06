namespace Nevermind.Network
{
    public class PingMessage : P2PMessage
    {
        public static PingMessage Instance = new PingMessage();

        private PingMessage()
        {
        }

        public override int MessageId => MessageCode.Ping;
    }
}