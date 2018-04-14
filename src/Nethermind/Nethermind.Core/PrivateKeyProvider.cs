using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public class PrivateKeyProvider : IPrivateKeyProvider
    {
        public PrivateKeyProvider(ICryptoRandom cryptoRandom)
        {
            PrivateKey = new PrivateKey(cryptoRandom.GenerateRandomBytes(32));
        }

        public PrivateKeyProvider(PrivateKey privateKey)
        {
            PrivateKey = privateKey;
        }

        public PrivateKey PrivateKey { get; }
    }
}