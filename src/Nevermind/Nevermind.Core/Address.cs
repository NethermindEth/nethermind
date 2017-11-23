using System;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;

namespace Nevermind.Core
{
    // can change it to behave as an array wrapper
    public class Address : IEquatable<Address>
    {
        private const int AddressLengthInBytes = 20;

        public readonly Hex Hex;

        public Address(Keccak keccak)
            : this(keccak.Bytes.Slice(12, 20))
        {
        }

        public Address(Hex hex)
        {
            if (hex == null)
            {
                throw new ArgumentNullException(nameof(hex));
            }

            if (hex.ByteLength != AddressLengthInBytes)
            {
                throw new ArgumentException($"{nameof(Address)} should be {AddressLengthInBytes} bytes long", nameof(hex));
            }

            Hex = hex;
        }

        public static Address Zero { get; } = new Address(new byte[20]);

        public bool Equals(Address other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            return Equals(Hex, other.Hex);
        }

        public string ToString(bool withEip55Checksum)
        {
            // use inside hex?
            return string.Concat("0x", Hex.FromBytes(Hex, false, false, withEip55Checksum));
        }

        /// <summary>
        ///     https://github.com/ethereum/EIPs/issues/55
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return ToString(false);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }
            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            return obj.GetType() == GetType() && Equals((Address)obj);
        }

        public override int GetHashCode()
        {
            return Hex.GetHashCode();
        }
    }
}