using System;

namespace Nevermind.Core
{
    public class Address
    {
        private readonly byte[] _address;

        public Address(string hexString)
            :this(HexString.ToBytes(hexString.Replace("0x", String.Empty)))
        {
        }

        public Address(byte[] bytes)
        {
            _address = bytes;
        }

        public string ToString(bool withEip55Checksum)
        {
            return string.Concat("0x", HexString.FromBytes(_address, withEip55Checksum));
        }

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
