using System;
using System.Threading;

namespace Nevermind.Core
{
    public class PrivateKey
    {
        private readonly byte[] _key;
        private PublicKey _publicKey;

        public PrivateKey(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != 32)
            {
                throw new ArgumentException("Invalid private key length", nameof(bytes));
            }

            _key = bytes;
        }

        private PublicKey ComputePublicKey()
        {
            byte[] bytes = _key;
            throw new NotImplementedException();
        }

        public PublicKey PublicKey => LazyInitializer.EnsureInitialized(ref _publicKey, ComputePublicKey);

        public override string ToString()
        {
            return HexString.FromBytes(_key);
        }
    }
}