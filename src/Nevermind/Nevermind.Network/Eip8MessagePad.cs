using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Network
{
    public class Eip8MessagePad : IMessagePad
    {
        readonly ICryptoRandom _cryptoRandom;

        public Eip8MessagePad(ICryptoRandom cryptoRandom)
        {
            _cryptoRandom = cryptoRandom;
        }

        public byte[] Pad(byte[] message)
        {
            byte[] padding = _cryptoRandom.GenerateRandomBytes(100 + _cryptoRandom.NextInt(201));
            return Bytes.Concat(message, padding);
        }
    }
}