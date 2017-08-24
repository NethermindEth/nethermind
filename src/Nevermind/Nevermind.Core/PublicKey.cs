using System;
using System.Threading;

namespace Nevermind.Core
{
    public class PublicKey
    {
        private readonly byte[] _publicKey;
        private Address _address;

        public PublicKey(byte[] bytes)
        {
            _publicKey = bytes;
            throw new NotImplementedException();
        }

        private Address ComputeAddress()
        {
            byte[] hash = Keccak.Compute(_publicKey);
            // get 160 bits
            throw new NotImplementedException();
        }

        public Address Address => LazyInitializer.EnsureInitialized(ref _address, ComputeAddress);

        public override string ToString()
        {
            return HexString.FromBytes(_publicKey);
        }
    }
}