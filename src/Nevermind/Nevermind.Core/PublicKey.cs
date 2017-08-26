using System;
using System.Threading;

namespace Nevermind.Core
{
    internal class PublicKey
    {
        private const int PublicKeyWithPrefixLengthInBytes = 65;
        private const int PublicKeyLengthInBytes = 64;
        private Address _address;

        public PublicKey(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != PublicKeyLengthInBytes && bytes.Length != PublicKeyWithPrefixLengthInBytes)
            {
                throw new ArgumentException($"{nameof(PublicKey)} should be {PublicKeyLengthInBytes} bytes long", nameof(bytes));
            }

            if (bytes.Length == PublicKeyWithPrefixLengthInBytes && bytes[0] != 0x04)
            {
                throw new ArgumentException($"Expected prefix of 0x04 for {PublicKeyWithPrefixLengthInBytes} bytes long {nameof(PublicKey)}");
            }

            Array.Copy(bytes, bytes.Length - PublicKeyLengthInBytes, Bytes, 0, 64);
        }

        private Address ComputeAddress()
        {
            byte[] hash = Keccak.Compute(Bytes);
            byte[] last160Bits = new byte[20];
            Array.Copy(hash, 12, last160Bits, 0, 20);
            return new Address(last160Bits);
        }

        public Address Address => LazyInitializer.EnsureInitialized(ref _address, ComputeAddress);

        public byte[] Bytes { get; } = new byte[64];
    }
}