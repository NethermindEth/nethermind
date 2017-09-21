using System;
using System.Diagnostics;

namespace Nevermind.Core.Encoding
{
    /// <summary>
    /// https://stackoverflow.com/questions/311165/how-do-you-convert-a-byte-array-to-a-hexadecimal-string-and-vice-versa
    /// </summary>
    // from performance / memory perspective probably better to remove this class entirely, experimental
    public class Hex : IEquatable<Hex>
    {
        private string _hexString;
        private byte[] _bytes;

        public int ByteLength => _bytes?.Length ?? _hexString.Length / 2;
        public int StringLenght => _hexString?.Length ?? _bytes.Length * 2;

        public Hex(string hexString)
        {
            _hexString = hexString.StartsWith("0x") ? hexString.Substring(2) : hexString;
        }

        public Hex(byte[] bytes)
        {
            _bytes = bytes;
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToString(bool withZeroX)
        {
            if (_hexString == null)
            {
                _hexString = FromBytes(_bytes, false);
            }

            return withZeroX ? string.Concat("0x", _hexString) : _hexString;
        }

        public static implicit operator byte[](Hex hex)
        {
            return hex._bytes ?? (hex._bytes = ToBytes(hex._hexString));
        }

        public static implicit operator string(Hex hex)
        {
            return hex.ToString(false);
        }

        public static implicit operator Hex(string hex)
        {
            if (hex == null)
            {
                return null;
            }

            return new Hex(hex);
        }

        public static implicit operator Hex(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }

            return new Hex(bytes);
        }

        public override bool Equals(object obj)
        {
            if (!(obj is Hex))
            {
                return false;
            }

            return Equals((Hex)obj);
        }

        public bool Equals(Hex obj)
        {
            if (obj == null)
            {
                return false;
            }

            if (_hexString != null && obj._hexString != null)
            {
                return _hexString == obj;
            }

            if (_hexString != null && obj._hexString == null)
            {
                return _hexString == obj;
            }

            if (_hexString == null && obj._hexString == null)
            {
                return this == (string)obj;
            }

            Debug.Assert(false, "one of the conditions should be true");
            return false;
        }

        public override int GetHashCode()
        {
            // ReSharper disable once NonReadonlyMemberInGetHashCode
            return (_hexString ?? this).GetHashCode();
        }

        private static readonly uint[] Lookup32 = CreateLookup32("x2");

        private static uint[] CreateLookup32(string format)
        {
            uint[] result = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                string s = i.ToString(format);
                result[i] = s[0] + ((uint)s[1] << 16);
            }
            return result;
        }

        private static string ByteArrayToHexViaLookup32(byte[] bytes, bool withZeroX, bool withEip55Checksum)
        {
            char[] result = new char[bytes.Length * 2 + (withZeroX ? 2 : 0)];
            string hashHex = null;
            if (withEip55Checksum)
            {
                // I guess it may be better (faster) than calling ToString here
                hashHex = Keccak.Compute(System.Text.Encoding.UTF8.GetBytes(FromBytes(bytes, false))).ToString(false);
            }

            if (withZeroX)
            {
                result[0] = '0';
                result[1] = 'x';
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                uint val = Lookup32[bytes[i]];
                char char1 = (char)val;
                char char2 = (char)(val >> 16);
                result[2 * i+ (withZeroX ? 2 : 0)] = withEip55Checksum && Char.IsLetter(char1) && hashHex[2 * i] > '7' ? Char.ToUpper(char1) : char1;
                result[2 * i + 1 + (withZeroX ? 2 : 0)] = withEip55Checksum && Char.IsLetter(char2) && hashHex[2 * i + 1] > '7' ? Char.ToUpper(char2) : char2;
            }

            return new string(result);
        }

        public static string FromBytes(byte[] bytes, bool withZeroX)
        {
            return FromBytes(bytes, withZeroX, false);
        }

        public static string FromBytes(byte[] bytes, bool withZeroX, bool withEip55Checksum)
        {
            return ByteArrayToHexViaLookup32(bytes, withZeroX, withEip55Checksum);
        }

        public static byte[] ToBytes(string hexString)
        {
            if (hexString == null)
            {
                throw new ArgumentNullException($"{nameof(hexString)}");
            }

            int startIndex = hexString.StartsWith("0x") ? 2 : 0;
            int numberChars = hexString.Length - startIndex;
            byte[] bytes = new byte[numberChars / 2];
            for (int i = 0; i < numberChars; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hexString.Substring(i + startIndex, 2), 16);
            }

            return bytes;
        }
    }
}
