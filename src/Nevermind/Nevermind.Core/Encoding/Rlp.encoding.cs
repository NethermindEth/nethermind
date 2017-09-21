using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nevermind.Core.Sugar;

namespace Nevermind.Core.Encoding
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
    // clean heap allocations (new Rlp())
    [DebuggerStepThrough]
    // https://github.com/ethereum/wiki/wiki/RLP
    public partial class Rlp
    {
        public static object Deserialize(Rlp rlp)
        {
            return Deserialize(new DeserializationContext(rlp.Bytes));
        }

        private static object Deserialize(DeserializationContext context, bool check = true)
        {
            object CheckAndReturn(List<object> resultToCollapse, DeserializationContext contextToCheck)
            {
                if (check && contextToCheck.CurrentIndex != contextToCheck.MaxIndex)
                {
                    throw new InvalidOperationException();
                }

                if (resultToCollapse.Count == 1)
                {
                    return resultToCollapse[0];
                }

                return resultToCollapse.ToArray();
            }

            List<object> result = new List<object>();

            byte prefix = context.Pop();

            if (prefix == 0)
            {
                result.Add(new byte[] { 0 });
                return CheckAndReturn(result, context);
            }

            if (prefix < 128)
            {
                result.Add(prefix);
                return CheckAndReturn(result, context);
            }

            if (prefix == 128)
            {
                result.Add(new byte[] {});
                return CheckAndReturn(result, context);
            }

            if (prefix <= 183)
            {
                int length = prefix - 128;
                byte[] data = context.Pop(length);
                if (data.Length == 1 && data[0] < 128)
                {
                    throw new InvalidOperationException();
                }

                result.Add(data);
                return CheckAndReturn(result, context);
            }

            if (prefix < 192)
            {
                int lengthOfLength = prefix - 183;
                if (lengthOfLength > 4)
                {
                    // strange but needed to pass tests -seems that spec gives int64 length and tests int32 length
                    throw new InvalidOperationException();
                }

                int length = DeserializeLength(context.Pop(lengthOfLength));
                if (length < 56)
                {
                    throw new InvalidOperationException();
                }

                byte[] data = context.Pop(length);
                if (data[0] == 0)
                {
                    throw new InvalidOperationException();
                }

                result.Add(data);
                return CheckAndReturn(result, context);
            }

            int concatenationLength;
            if (prefix <= 247)
            {
                concatenationLength = prefix - 192;
            }
            else
            {
                int lengthOfConcatenationLength = prefix - 247;
                if (lengthOfConcatenationLength > 4)
                {
                    // strange but needed to pass tests -seems that spec gives int64 length and tests int32 length
                    throw new InvalidOperationException();
                }

                concatenationLength = DeserializeLength(context.Pop(lengthOfConcatenationLength));
                if (concatenationLength < 56)
                {
                    throw new InvalidOperationException();
                }
            }

            long startIndex = context.CurrentIndex;
            List<object> nestedList = new List<object>();
            while (context.CurrentIndex < startIndex + concatenationLength)
            {
                nestedList.Add(Deserialize(context, false));
            }

            result.Add(nestedList.ToArray());

            return CheckAndReturn(result, context);
        }

        // TODO: streams and proper encodings
        public class DeserializationContext
        {
            public DeserializationContext(byte[] data)
            {
                Data = data;
                MaxIndex = Data.Length;
            }

            public byte Pop()
            {
                return Data[CurrentIndex++];
            }

            public byte[] Pop(int n)
            {
                byte[] bytes = new byte[n];
                Buffer.BlockCopy(Data, CurrentIndex, bytes, 0, n);
                CurrentIndex += n;
                return bytes;
            }

            public byte[] Data { get; }
            public int CurrentIndex { get; set; }
            public int MaxIndex { get; set; }
        }

        public static int DeserializeLength(byte[] bytes)
        {
            const int size = sizeof(Int32);
            byte[] padded = new byte[size];
            Array.Copy(bytes, 0, padded, size - bytes.Length, bytes.Length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(padded);
            }

            return BitConverter.ToInt32(padded, 0);
        }

        public static Rlp OfEmptySequence => Serialize();

        [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
        public static Rlp Serialize(params object[] sequence)
        {
            byte[] concatenation = new byte[0];
            foreach (object item in sequence)
            {
                // do that at once (unnecessary objects creation here)
                concatenation = Concat(concatenation, Serialize(item).Bytes);
            }

            if (concatenation.Length < 56)
            {
                return new Rlp(Concat((byte)(192 + concatenation.Length), concatenation));
            }

            byte[] serializedLength = SerializeLength(concatenation.Length);
            byte prefix = (byte)(247 + serializedLength.Length);
            return new Rlp(Concat(prefix, serializedLength, concatenation));
        }

        private static byte[] Concat(byte prefix, byte[] x)
        {
            if (x == null) throw new ArgumentNullException(nameof(x));

            byte[] output = new byte[1 + x.Length];
            output[0] = prefix;
            Buffer.BlockCopy(x, 0, output, 1, x.Length);
            return output;
        }

        private static byte[] Concat(byte[] x, byte[] y)
        {
            if (x == null) throw new ArgumentNullException(nameof(x));
            if (y == null) throw new ArgumentNullException(nameof(y));

            byte[] output = new byte[x.Length + y.Length];
            Buffer.BlockCopy(x, 0, output, 0, x.Length);
            Buffer.BlockCopy(y, 0, output, x.Length, y.Length);
            return output;
        }

        private static byte[] Concat(byte prefix, byte[] x, byte[] y)
        {
            if (x == null) throw new ArgumentNullException(nameof(x));
            if (y == null) throw new ArgumentNullException(nameof(y));

            byte[] output = new byte[1 + x.Length + y.Length];
            output[0] = prefix;
            Buffer.BlockCopy(x, 0, output, 1, x.Length);
            Buffer.BlockCopy(y, 0, output, 1 + x.Length, y.Length);
            return output;
        }

        public static Rlp Serialize(object item)
        {
            object[] objects = item as object[];
            if (objects != null)
            {
                return Serialize(objects);
            }

            byte[] byteArray = item as byte[];
            if (byteArray != null)
            {
                return Serialize(byteArray);
            }

            if (item is BigInteger)
            {
                return Serialize(((BigInteger)item).ToBigEndianByteArray());
            }

            if (item is byte || item is short || item is int || item is ushort || item is uint)
            {
                return Serialize(Convert.ToInt64(item));
            }

            // can use serialize length here and wrap in the byte array serialization
            if (item is long)
            {
                long value = (long)item;

                // check test bytestring00 and zero - here is some inconsistency in tests
                if (value == 0L)
                {
                    return new Rlp(128);
                }

                if (value < 128L)
                {
                    // ReSharper disable once PossibleInvalidCastException
                    return new Rlp(Convert.ToByte(value));
                }

                if (value <= Byte.MaxValue)
                {
                    // ReSharper disable once PossibleInvalidCastException
                    return Serialize(new[] { Convert.ToByte(value) });
                }

                if (value <= Int16.MaxValue)
                {
                    // ReSharper disable once PossibleInvalidCastException
                    return Serialize(((short)value).ToBigEndianByteArray());
                }

                return Serialize(new BigInteger(value));
            }

            string s = item as string;
            if (s != null)
            {
                return Serialize(System.Text.Encoding.ASCII.GetBytes(s));
            }

            throw new NotSupportedException($"RLP does not support items of type {item.GetType().Name}");
        }

        private static Rlp Serialize(Rlp input)
        {
            return Serialize(input.Bytes);
        }

        public static Rlp Serialize(byte[] input)
        {
            if (input.Length == 0)
            {
                return new Rlp(128);
            }

            if (input.Length == 1 && input[0] < 128)
            {
                return new Rlp(input[0]);
            }

            if (input.Length < 56)
            {
                byte smallPrefix = (byte)(input.Length + 128);
                return new Rlp(Concat(smallPrefix, input));
            }

            byte[] serializedLength = SerializeLength(input.Length);
            byte prefix = (byte)(183 + serializedLength.Length);
            return new Rlp(Concat(prefix, serializedLength, input));
        }

        public static byte[] SerializeLength(long value)
        {
            const int maxResultLength = 8;
            byte[] bytes = new byte[maxResultLength];

            bytes[0] = (byte)(value >> 56);
            bytes[1] = (byte)(value >> 48);
            bytes[2] = (byte)(value >> 40);
            bytes[3] = (byte)(value >> 32);
            bytes[4] = (byte)(value >> 24);
            bytes[5] = (byte)(value >> 16);
            bytes[6] = (byte)(value >> 8);
            bytes[7] = (byte)value;

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
            Buffer.BlockCopy(bytes, maxResultLength - resultLength, result, 0, resultLength);
            return result;
        }
    }
}
