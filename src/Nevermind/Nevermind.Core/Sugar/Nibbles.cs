using System;
using System.Diagnostics;

namespace Nevermind.Core.Sugar
{
    [DebuggerStepThrough]
    public static class Nibbles
    {
        public static byte ToByte(byte highNibble, byte lowNibble)
        {
            return (byte)(highNibble << 4 | lowNibble);
        }

        public static byte[] FromBytes(params byte[] bytes)
        {
            byte[] nibbles = new byte[2 * bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                nibbles[i * 2] = (byte)(bytes[i] & 240);
                nibbles[i * 2 + 1] = (byte)(bytes[i] & 15);
            }

            return nibbles;
        }

        public static byte[] FromBytes(byte @byte)
        {
            return new[] { (byte)(@byte & 240), (byte)(@byte & 15) };
        }

        public static byte[] ToBytes(params byte[] nibbles)
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
    }
}
