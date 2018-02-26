namespace Nevermind.Network.P2P
{
    public interface IMessageSender
    {
        void Enqueue<T>(T message, bool priority = false) where T : P2PMessage;
    }
}