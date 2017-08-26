using System;

namespace Nevermind.Core
{
    public class Address
    {
        public Address(string hexString)
            :this(HexString.ToBytes(hexString.Replace("0x", String.Empty)))
        {
        }

        private const int AddressLengthInBytes = 20;

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
    }
}
