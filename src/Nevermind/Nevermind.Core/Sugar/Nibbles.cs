using System;

namespace Nevermind.Core.Sugar
{
    public static class Nibbles
    {
        public static Nibble[] FromBytes(params byte[] bytes)
        {
            Nibble[] nibbles = new Nibble[2 * bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                nibbles[i * 2] = new Nibble((byte) ((bytes[i] & 240) >> 4));
                nibbles[i * 2 + 1] = new Nibble((byte) (bytes[i] & 15));
            }

            return nibbles;
        }

        public static Nibble[] FromBytes(byte @byte)
        {
            return new[] {new Nibble((byte) (@byte & 240)), new Nibble((byte) (@byte & 15))};
        }

        public static byte[] ToLooseByteArray(this Nibble[] nibbles)
        {
            byte[] bytes = new byte[nibbles.Length];
            for (int i = 0; i < nibbles.Length; i++)
            {
                bytes[i] = (byte) nibbles[i];
            }

            return bytes;
        }

        public static byte[] ToPackedByteArray(this Nibble[] nibbles)
        {
            if (nibbles.Length % 2 != 0)
            {
                throw new ArgumentException(nameof(nibbles));
            }

            byte[] bytes = new byte[nibbles.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = ToByte(nibbles[2 * i], nibbles[2 * i + 1]);
            }

            return bytes;
        }

        public static byte ToByte(Nibble highNibble, Nibble lowNibble)
        {
            return (byte) (((byte)highNibble << 4) | (byte)lowNibble);
        }
    }
}