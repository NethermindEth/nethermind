using System;
using Nevermind.Core.Encoding;

namespace Nevermind.Core
{
    public class Address : IEquatable<Address>
    {
        public Address(string hexString)
            :this(HexString.ToBytes(hexString.Replace("0x", String.Empty)))
        {
        }

        private const int AddressLengthInBytes = 20;

        public static Address Zero { get; } = new Address(new byte[20]); 

        public Address(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            if (bytes.Length != AddressLengthInBytes)
            {
                throw new ArgumentException($"{nameof(Address)} should be {AddressLengthInBytes} bytes long", nameof(bytes));
            }

            Bytes = bytes;
        }

        public string ToString(bool withEip55Checksum)
        {
            return string.Concat("0x", HexString.FromBytes(Bytes, withEip55Checksum));
        }

        public byte[] Bytes { get; }

        /// <summary>
        /// https://github.com/ethereum/EIPs/issues/55
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return ToString(false);
        }

        public bool Equals(Address other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Equals(ToString(), other.ToString());
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((Address) obj);
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }
    }
}
