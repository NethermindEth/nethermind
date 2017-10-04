using System;
using System.Data.HashFunction;
using Nevermind.Core.Encoding;

namespace Nevermind.Core.Sugar
{
    public static class ByteArrayExtensions
    {
        private static readonly xxHash XxHash = new xxHash(32);

        public static string ToHex(this byte[] bytes, bool withZeroX = true)
        {
            return Hex.FromBytes(bytes, withZeroX);
        }

        public static byte[] Slice(this byte[] bytes, int startIndex, int length)
        {
            if (length == 1)
            {
                return new[] {bytes[startIndex]};
            }

            byte[] slice = new byte[length];
            Buffer.BlockCopy(bytes, startIndex, slice, 0, length);
            return slice;
        }

        public static int GetXxHashCode(this byte[] bytes)
        {
            return BitConverter.ToInt32(XxHash.ComputeHash(bytes), 0);
        }
    }
}