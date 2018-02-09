namespace Nevermind.Network
{
    public interface IMessageFactory<out T> where T : MessageBase
    {
        T Create(int protocolType, int packetType, byte[] serializedData);
    }
}