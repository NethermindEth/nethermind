using System;
using System.Threading;
using Nevermind.Core.Extensions;

namespace Nevermind.Core.Crypto
{
    public class PublicKey : IEquatable<PublicKey>
    {
        private const int PublicKeyWithPrefixLengthInBytes = 65;
        private const int PublicKeyLengthInBytes = 64;
        private Address _address;

        public PublicKey(byte[] bytes, byte[] compressedBytes)
            : this(bytes)
        {
            CompressedBytes = compressedBytes;
        }

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

            PrefixedBytes[0] = 0x04;
            Buffer.BlockCopy(bytes, bytes.Length - PublicKeyLengthInBytes, PrefixedBytes, 1, 64);
        }

        private Address ComputeAddress()
        {
            byte[] bytes = new byte[64];
            Buffer.BlockCopy(PrefixedBytes, 1, bytes, 0, 64);
            byte[] hash = Keccak.Compute(bytes).Bytes;
            byte[] last160Bits = new byte[20];
            Buffer.BlockCopy(hash, 12, last160Bits, 0, 20);
            return new Address(last160Bits);
        }

        public Address Address => LazyInitializer.EnsureInitialized(ref _address, ComputeAddress);

        public byte[] PrefixedBytes { get; } = new byte[65];

        private byte[] CompressedBytes { get; }

        public bool Equals(PublicKey other)
        {
            if (other == null)
            {
                return false;
            }
            
            return Bytes.UnsafeCompare(PrefixedBytes, other.PrefixedBytes);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as PublicKey);
        }

        public override int GetHashCode()
        {
            return PrefixedBytes.GetXxHashCode();
        }

        public override string ToString()
        {
            return Hex.FromBytes(PrefixedBytes, true);
        }
    }
}