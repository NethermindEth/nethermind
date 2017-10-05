using System.Diagnostics;

namespace Nevermind.Core
{
    [DebuggerDisplay("{_nibble}")]
    public struct Nibble
    {
        private readonly byte _nibble;

        public Nibble(char hexChar)
        {
            hexChar = char.ToUpper(hexChar);
            _nibble = hexChar < 'A'? (byte) (hexChar - '0') : (byte) (10 + (hexChar - 'A'));
        }

        public Nibble(byte nibble)
        {
            _nibble = nibble;
        }

        public static explicit operator byte(Nibble nibble)
        {
            return nibble._nibble;
        }

        public static implicit operator Nibble(byte nibbleValue)
        {
            return new Nibble(nibbleValue);
        }

        public static implicit operator Nibble(char hexChar)
        {
            return new Nibble(hexChar);
        }
    }
}