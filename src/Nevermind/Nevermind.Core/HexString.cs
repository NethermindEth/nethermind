using System;
using System.Text;

namespace Nevermind.Core
{
    /// <summary>
    /// https://stackoverflow.com/questions/311165/how-do-you-convert-a-byte-array-to-a-hexadecimal-string-and-vice-versa
    /// </summary>
    public static class HexString
    {
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

        private static string ByteArrayToHexViaLookup32(byte[] bytes, bool withEip55Checksum)
        {
            char[] result = new char[bytes.Length * 2];
            string hashHex = null;
            if (withEip55Checksum)
            {
                hashHex = Keccak.Compute(Encoding.UTF8.GetBytes(FromBytes(bytes))).ToString();
            }

            for (int i = 0; i < bytes.Length; i++)
            {
                uint val = Lookup32[bytes[i]];
                char char1 = (char)val;
                char char2 = (char)(val >> 16);
                result[2 * i] = withEip55Checksum && Char.IsLetter(char1) && hashHex[2 * i] > '7' ? Char.ToUpper(char1) : char1;
                result[2 * i + 1] = withEip55Checksum && Char.IsLetter(char2) && hashHex[2 * i + 1] > '7' ? Char.ToUpper(char2) : char2;
            }

            return new string(result);
        }

        public static string FromBytes(byte[] bytes)
        {
            return FromBytes(bytes, false);
        }

        public static string FromBytes(byte[] bytes, bool withEip55Checksum)
        {
            return ByteArrayToHexViaLookup32(bytes, withEip55Checksum);
        }

        public static byte[] ToBytes(string hexString)
        {
            if (hexString == null)
            {
                throw new ArgumentNullException($"{nameof(hexString)}");
            }

            int numberChars = hexString.Length;
            byte[] bytes = new byte[numberChars / 2];
            for (int i = 0; i < numberChars; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hexString.Substring(i, 2), 16);
            }

            return bytes;
        }
    }
}
