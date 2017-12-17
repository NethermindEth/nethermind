namespace Nevermind.Network
{
    public class DisconnectMessage : P2PMessage
    {
        public DisconnectMessage(DisconnectReason reason)
        {
            Reason = reason;
        }

        public override int MessageId => MessageCode.Disconnect;
        public DisconnectReason Reason { get; set; }
    }
}