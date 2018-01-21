using Nevermind.Core.Crypto;
using Nevermind.Discovery.Messages;

namespace Nevermind.Discovery.Encoders
{
    public interface IMessageEncoder
    {
        Message Decode(byte[] content);
        byte[] Encode(MessageType type, byte[] data, PrivateKey privateKey);
    }
}