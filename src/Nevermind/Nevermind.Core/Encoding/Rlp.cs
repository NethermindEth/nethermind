using System;
using System.Collections.Generic;
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
    //[DebuggerStepThrough]
    // https://github.com/ethereum/wiki/wiki/RLP
    public partial class Rlp : IEquatable<Rlp>
    {
        public static object Decode(Rlp rlp)
        {
            return Decode(new DecoderContext(rlp.Bytes));
        }

        private static object Decode(DecoderContext context, bool check = true)
        {
            object CheckAndReturn(List<object> resultToCollapse, DecoderContext contextToCheck)
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
                result.Add(new [] {prefix});
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
                nestedList.Add(Decode(context, false));
            }

            result.Add(nestedList.ToArray());

            return CheckAndReturn(result, context);
        }

        // TODO: streams and proper encodings
        public class DecoderContext
        {
            public DecoderContext(byte[] data)
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

        [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
        public static Rlp Encode(params object[] sequence)
        {
            byte[] concatenation = new byte[0];
            foreach (object item in sequence)
            {
                byte[] itemBytes = Encode(item).Bytes;
                // do that at once (unnecessary objects creation here)
                concatenation = Sugar.Bytes.Concat(concatenation, itemBytes);
            }

            if (concatenation.Length < 56)
            {
                return new Rlp(Sugar.Bytes.Concat((byte)(192 + concatenation.Length), concatenation));
            }

            byte[] serializedLength = SerializeLength(concatenation.Length);
            byte prefix = (byte)(247 + serializedLength.Length);
            return new Rlp(Sugar.Bytes.Concat(prefix, serializedLength, concatenation));
        }

        public static Rlp Encode(BigInteger bigInteger)
        {
            return bigInteger == 0 ? OfEmptyByteArray : Encode(bigInteger.ToBigEndianByteArray());
        }

        public static Rlp Encode(object item)
        {
            // will this work?
            if (item == null)
            {
                return OfEmptyByteArray;
            }

            // code below was written at the beginning - needs some rewrite

            if (item is Rlp rlp)
            {
                return Encode(rlp);
            }

            if (item is object[] objects)
            {
                return Encode(objects);
            }

            if (item is byte[] byteArray)
            {
                return Encode(byteArray);
            }

            if (item is Keccak keccak)
            {
                return Encode(keccak);
            }

            if (item is Address address)
            {
                return Encode(address);
            }

            if (item is LogEntry logEntry)
            {
                return Encode(logEntry);
            }

            if (item is BigInteger bigInt)
            {
                return Encode(bigInt);
            }

            if (item is BlockHeader header)
            {
                return Encode(header);
            }

            if (item is Bloom bloom)
            {
                return Encode(bloom);
            }

            if (item is ulong ulongNumber)
            {
                return Encode(ulongNumber.ToBigEndianByteArray());
            }

            if (item is byte || item is short || item is int || item is ushort || item is uint)
            {
                return Encode((object) Convert.ToInt64(item));
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

                if (value <= byte.MaxValue)
                {
                    // ReSharper disable once PossibleInvalidCastException
                    return Encode(new[] { Convert.ToByte(value) });
                }

                if (value <= short.MaxValue)
                {
                    // ReSharper disable once PossibleInvalidCastException
                    return Encode(((short)value).ToBigEndianByteArray());
                }

                return Encode(new BigInteger(value));
            }

            if (item is string s)
            {
                return Encode(s);
            }

            throw new NotSupportedException($"RLP does not support items of type {item.GetType().Name}");
        }

        private static Rlp Encode(string s)
        {
            return Encode(System.Text.Encoding.ASCII.GetBytes(s));
        }

        private static Rlp Encode(Rlp rlp)
        {
            //return Encode(rlp.Bytes);
            return rlp;
        }

        public static Rlp Encode(byte[] input)
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
                return new Rlp(Sugar.Bytes.Concat(smallPrefix, input));
            }

            byte[] serializedLength = SerializeLength(input.Length);
            byte prefix = (byte)(183 + serializedLength.Length);
            return new Rlp(Sugar.Bytes.Concat(prefix, serializedLength, input));
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

        public static readonly Rlp OfEmptyByteArray = new Rlp(128);

        public static Rlp OfEmptySequence = Encode();

        private static readonly Dictionary<RuntimeTypeHandle, IRlpDecoder> Decoders =
            new Dictionary<RuntimeTypeHandle, IRlpDecoder>
            {
                [typeof(Transaction).TypeHandle] = new TransactionDecoder(),
                [typeof(Account).TypeHandle] = new AccountDecoder()
            };

        public Rlp(byte singleByte)
        {
            Bytes = new[] { singleByte };
        }

        public Rlp(byte[] bytes)
        {
            Bytes = bytes;
        }

        public byte[] Bytes { get; }

        public byte this[int index] => Bytes[index];
        public int Length => Bytes.Length;

        public bool Equals(Rlp other)
        {
            if (other == null)
            {
                return false;
            }

            return Sugar.Bytes.UnsafeCompare(Bytes, other.Bytes);
        }

        public static Rlp Encode(BlockHeader header)
        {
            return Encode(
                header.ParentHash,
                header.OmmersHash,
                header.Beneficiary,
                header.StateRoot,
                header.TransactionsRoot,
                header.ReceiptsRoot,
                header.LogsBloom,
                header.Difficulty,
                header.Number,
                header.GasLimit,
                header.GasUsed,
                header.Timestamp,
                header.ExtraData,
                header.MixHash,
                header.Nonce
            );
        }

        public static Rlp Encode(Block block)
        {
            return Encode(block.Header, block.Transactions, block.Ommers);
        }

        public static Rlp Encode(Bloom bloom)
        {
            byte[] result = new byte[259];
            result[0] = 185;
            result[1] = 1;
            result[2] = 0;
            Buffer.BlockCopy(bloom.Bytes, 0, result, 3, 256);
            return new Rlp(result);
        }

        public static Rlp Encode(LogEntry logEntry)
        {
            return Encode(
                logEntry.LoggersAddress,
                logEntry.Topics,
                logEntry.Data);
        }

        public static Rlp Encode(Account account)
        {
            return Encode(
                account.Nonce,
                account.Balance,
                account.StorageRoot,
                account.CodeHash);
        }

        public static Rlp Encode(TransactionReceipt receipt)
        {
            return Encode(
                receipt.PostTransactionState,
                receipt.GasUsed,
                receipt.Bloom,
                receipt.Logs);
        }

        public static T Decode<T>(Rlp rlp)
        {
            if (Decoders.ContainsKey(typeof(T).TypeHandle))
            {
                return ((IRlpDecoder<T>)Decoders[typeof(T).TypeHandle]).Decode(rlp);
            }

            throw new NotImplementedException();
        }

        public static Rlp Encode(Transaction transaction, bool forSigning, bool eip155 = false, int chainId = 0)
        {
            object[] sequence = new object[forSigning && !eip155 ? 6 : 9];
            sequence[0] = transaction.Nonce;
            sequence[1] = transaction.GasPrice;
            sequence[2] = transaction.GasLimit;
            sequence[3] = transaction.To;
            sequence[4] = transaction.Value;
            sequence[5] = transaction.To == null ? transaction.Init : transaction.Data;

            if (forSigning)
            {
                if (eip155)
                {
                    sequence[6] = chainId;
                    sequence[7] = BigInteger.Zero;
                    sequence[8] = BigInteger.Zero;
                }
            }
            else
            {
                sequence[6] = transaction.Signature?.V;
                sequence[7] = transaction.Signature?.R;
                sequence[8] = transaction.Signature?.S;
            }

            return Encode(sequence);
        }

        public static Rlp Encode(Transaction transaction)
        {
            return Encode(transaction, false);
        }

        public static Rlp Encode(Keccak keccak)
        {
            byte[] result = new byte[33];
            result[0] = 160;
            Buffer.BlockCopy(keccak.Bytes, 0, result, 1, 32);
            return new Rlp(result);
        }

        public static Keccak DecodeKeccak(Rlp rlp)
        {
            return new Keccak(rlp.Bytes.Slice(1, 32));
        }

        public static Rlp Encode(Address address)
        {
            if (address == null)
            {
                return OfEmptyByteArray;
            }

            byte[] result = new byte[21];
            result[0] = 148;
            Buffer.BlockCopy(address.Hex, 0, result, 1, 20);
            return new Rlp(result);
        }

        public string ToString(bool withZeroX)
        {
            return Hex.FromBytes(Bytes, withZeroX);
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public int GetHashCode(Rlp obj)
        {
            return obj.Bytes.GetXxHashCode();
        }
    }
}
