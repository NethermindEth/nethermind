using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Nevermind.Core
{
    /// <summary>
    /// If the value to be serialised is a byte-array, the RLP serialisation takes one of three forms:
    ///   • If the byte-array contains solely a single byte and that single byte is less than 128,
    ///     then the input is exactly equal to the output.
    ///   • If the byte-array contains fewer than 56 bytes,
    ///     then the output is equal to the input preﬁxed by the byte equal to the length of the byte array plus 128.
    ///   • Otherwise, the output is equal to the input preﬁxed by the minimal-length byte-array
    ///     which when interpreted as a big-endian integer is equal to the length of the input byte array,
    ///     which is itself preﬁxed by the number of bytes required to faithfully encode this length value plus 183.
    /// 
    /// If instead, the value to be serialised is a sequence of other items then the RLP serialisation takes one of two forms:
    ///   • If the concatenated serialisations of each contained item is less than 56 bytes in length,
    ///     then the output is equal to that concatenation preﬁxed by the byte equal to the length of this byte array plus 192.
    ///   • Otherwise, the output is equal to the concatenated serialisations preﬁxed by the minimal-length byte-array
    ///     which when interpreted as a big-endian integer is equal to the length of the concatenated serialisations byte array,
    ///     which is itself preﬁxed by the number of bytes required to faithfully encode this length value plus 247. 
    /// </summary>
    public static class RecursiveLengthPrefix
    {
        [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
        public static byte[] Serialize(IEnumerable<object> sequence)
        {
            object firstItem = sequence.FirstOrDefault();
            if (firstItem == null)
            {
                return Serialize(new byte[0]);
            }

            byte[] concatenation = new byte[0];
            foreach (object item in sequence)
            {
                concatenation = Concat(concatenation, Serialize(item));
            }

            if (concatenation.Length < 56)
            {
                return Concat((byte)(192 + concatenation.Length), concatenation);
            }

            byte[] serializedLength = Serialize(concatenation.Length);
            byte prefix = (byte) (247 + serializedLength.Length);
            return Concat(prefix, serializedLength, concatenation);
        }

        public static byte[] Concat(byte prefix, byte[] x)
        {
            if (x == null) throw new ArgumentNullException(nameof(x));

            byte[] output = new byte[1 + x.Length];
            output[0] = prefix;
            Array.Copy(x, 0, output, 1, x.Length);
            return output;
        }

        public static byte[] Concat(byte[] x, byte[] y)
        {
            if (x == null) throw new ArgumentNullException(nameof(x));
            if (y == null) throw new ArgumentNullException(nameof(y));

            byte[] output = new byte[x.Length + y.Length]; 
            Array.Copy(x, 0, output, 0, x.Length);
            Array.Copy(y, 0, output, x.Length, y.Length);
            return output;
        }

        public static byte[] Concat(byte prefix, byte[] x, byte[] y)
        {
            if (x == null) throw new ArgumentNullException(nameof(x));
            if (y == null) throw new ArgumentNullException(nameof(y));

            byte[] output = new byte[1 + x.Length + y.Length];
            output[0] = prefix;
            Array.Copy(x, 0, output, 1, x.Length);
            Array.Copy(y, 0, output, 1 + x.Length, y.Length);
            return output;
        }

        public static byte[] Serialize(object item)
        {
            byte[] byteArray = item as byte[];
            if (byteArray != null)
            {
                return Serialize(byteArray);
            }

            if (item is long)
            {
                return Serialize((long)item);
            }

            throw new NotSupportedException($"{nameof(RecursiveLengthPrefix)} only supports {nameof(Int64)} and byte arrays");
        }

        public static byte[] Serialize(byte[] input)
        {
            if (input.Length == 1 && input[0] < 128)
            {
                return input;
            }

            if (input.Length < 56)
            {
                byte smallPrefix = (byte) (input.Length + 128);
                return Concat(smallPrefix, input);
            }

            byte[] serializedLength = Serialize(input.Length);
            byte prefix = (byte)(183 + serializedLength.Length);
            return Concat(prefix, serializedLength, input);
        }

        public static byte[] Serialize(long length)
        {
            if (length < 56)
            {
                throw new ArgumentException("Length for BigEndian is expected to be above 56", nameof(length));
            }

            const int maxResultLength = 8;
            byte[] bytes = new byte[maxResultLength];

            bytes[0] = (byte)(length >> 56);
            bytes[1] = (byte)(length >> 48);
            bytes[2] = (byte)(length >> 40);
            bytes[3] = (byte)(length >> 32);
            bytes[4] = (byte)(length >> 24);
            bytes[5] = (byte)(length >> 16);
            bytes[6] = (byte)(length >> 8);
            bytes[7] = (byte)length;

            int resultLength = maxResultLength;
            for (int i = 0; i < maxResultLength; i++)
            {
                if (bytes[i] == 0)
                {
                    resultLength--;
                }
                else
                {
                    break;
                }
            }

            byte[] result = new byte[resultLength];
            Array.Copy(bytes, maxResultLength - resultLength, result, 0, resultLength);
            return result;
        }
    }
}
