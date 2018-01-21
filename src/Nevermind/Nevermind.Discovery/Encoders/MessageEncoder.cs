using System;
using Nevermind.Core.Crypto;
using Nevermind.Discovery.Messages;

namespace Nevermind.Discovery.Encoders
{
    public class MessageEncoder : IMessageEncoder
    {
        public Message Decode(byte[] content)
        {
            throw new NotImplementedException();
        }

        public byte[] Encode(MessageType type, byte[] data, PrivateKey privateKey)
        {
            throw new NotImplementedException();
        }
    }
}