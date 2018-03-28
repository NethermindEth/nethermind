/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Encoding
{
    /// <summary>
    /// https://github.com/ethereum/wiki/wiki/RLP
    /// </summary>
    //[DebuggerStepThrough]
    public class Rlp : IEquatable<Rlp>
    {
        public static readonly Rlp OfEmptyByteArray = new Rlp(128);

        public static readonly Rlp OfEmptySequence = new Rlp(192);

        private static readonly Dictionary<RuntimeTypeHandle, IRlpDecoder> Decoders =
            new Dictionary<RuntimeTypeHandle, IRlpDecoder>
            {
                [typeof(Transaction).TypeHandle] = new TransactionDecoder(),
                [typeof(Account).TypeHandle] = new AccountDecoder(),
                [typeof(Block).TypeHandle] = new BlockDecoder(),
                [typeof(BlockHeader).TypeHandle] = new BlockHeaderDecoder()
            };

        public Rlp(byte singleByte)
        {
            Bytes = new[] {singleByte};
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

            return Extensions.Bytes.UnsafeCompare(Bytes, other.Bytes);
        }

        public static DecodedRlp Decode(Rlp rlp, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Decode(new DecoderContext(rlp.Bytes), rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData));
        }

        public static T Decode<T>(Rlp rlp, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (Decoders.ContainsKey(typeof(T).TypeHandle))
            {
                return ((IRlpDecoder<T>)Decoders[typeof(T).TypeHandle]).Decode(rlp);
            }

            return Decode(new DecoderContext(rlp.Bytes), rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData)).As<T>();
        }

        public static Rlp[] ExtractRlpList(Rlp rlp)
        {
            return ExtractRlpList(new DecoderContext(rlp.Bytes));
        }

        private static Rlp[] ExtractRlpList(DecoderContext context)
        {
            var result = new List<Rlp>();

            while (context.CurrentIndex < context.MaxIndex)
            {
                byte prefix = context.Pop();
                byte[] lenghtBytes = null;

                int concatenationLength;

                if (prefix == 0)
                {
                    result.Add(new Rlp(new byte[] {0}));
                    continue;
                }

                if (prefix < 128)
                {
                    result.Add(new Rlp(new[] {prefix}));
                    continue;
                }

                if (prefix == 128)
                {
                    result.Add(new Rlp(new byte[] { }));
                    continue;
                }

                if (prefix <= 183)
                {
                    int length = prefix - 128;
                    var content = context.Pop(length);
                    if (content.Length == 1 && content[0] < 128)
                    {
                        throw new RlpException($"Unexpected byte value {content[0]}");
                    }

                    result.Add(new Rlp(new[] {prefix}.Concat(content).ToArray()));
                    continue;
                }

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
                        throw new RlpException("Expected length of lenth less or equal 4");
                    }

                    lenghtBytes = context.Pop(lengthOfConcatenationLength);
                    concatenationLength = DeserializeLength(lenghtBytes);
                    if (concatenationLength < 56)
                    {
                        throw new RlpException("Expected length greater or equal 56");
                    }
                }

                var data = context.Pop(concatenationLength);
                var itemBytes = new[] {prefix};
                if (lenghtBytes != null)
                {
                    itemBytes = itemBytes.Concat(lenghtBytes).ToArray();
                }

                result.Add(new Rlp(itemBytes.Concat(data).ToArray()));
            }

            return result.ToArray();
        }

        // TODO: optimize so the list is not created for every single call to Rlp.Decode()
        // TODO: intorduce typed Encode / Decode

        private static DecodedRlp Decode(DecoderContext context, bool allowExtraData)
        {
            DecodedRlp CheckAndReturnSingle(object singleItem, DecoderContext contextToCheck)
            {
                if (!allowExtraData && contextToCheck.CurrentIndex != contextToCheck.MaxIndex)
                {
                    throw new RlpException("Invalid RLP length");
                }

                return new DecodedRlp(singleItem);
            }

            DecodedRlp CheckAndReturn(List<object> resultToCollapse, DecoderContext contextToCheck)
            {
                if (!allowExtraData && contextToCheck.CurrentIndex != contextToCheck.MaxIndex)
                {
                    throw new RlpException("Invalid RLP length");
                }

                return new DecodedRlp(resultToCollapse);
            }

            byte prefix = context.Pop();

            if (prefix == 0)
            {
                return CheckAndReturnSingle(new byte[] {0}, context);
            }

            if (prefix < 128)
            {
                return CheckAndReturnSingle(new[] {prefix}, context);
            }

            if (prefix == 128)
            {
                return CheckAndReturnSingle(new byte[] { }, context);
            }

            if (prefix <= 183)
            {
                int length = prefix - 128;
                byte[] data = context.Pop(length);
                if (data.Length == 1 && data[0] < 128)
                {
                    throw new RlpException($"Unexpected byte value {data[0]}");
                }

                return CheckAndReturnSingle(data, context);
            }

            if (prefix < 192)
            {
                int lengthOfLength = prefix - 183;
                if (lengthOfLength > 4)
                {
                    // strange but needed to pass tests -seems that spec gives int64 length and tests int32 length
                    throw new RlpException("Expected length of lenth less or equal 4");
                }

                int length = DeserializeLength(context.Pop(lengthOfLength));
                if (length < 56)
                {
                    throw new RlpException("Expected length greater or equal 56");
                }

                byte[] data = context.Pop(length);
                return CheckAndReturnSingle(data, context);
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
                    throw new RlpException("Expected length of lenth less or equal 4");
                }

                concatenationLength = DeserializeLength(context.Pop(lengthOfConcatenationLength));
                if (concatenationLength < 56)
                {
                    throw new RlpException("Expected length greater or equal 56");
                }
            }

            long startIndex = context.CurrentIndex;
            List<object> nestedList = new List<object>();
            while (context.CurrentIndex < startIndex + concatenationLength)
            {
                DecodedRlp decodedRlp = Decode(context, true);
                nestedList.Add(decodedRlp.IsSequence ? decodedRlp : decodedRlp.SingleItem);
            }

            return CheckAndReturn(nestedList, context);
        }

        public static int DeserializeLength(byte[] bytes)
        {
            if (bytes[0] == 0)
            {
                throw new RlpException("Length starts with 0");
            }

            const int size = sizeof(int);
            byte[] padded = new byte[size];
            Buffer.BlockCopy(bytes, 0, padded, size - bytes.Length, bytes.Length);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(padded);
            }

            return BitConverter.ToInt32(padded, 0);
        }

        public static Rlp Encode<T>(List<T> sequence)
        {
            Rlp[] rlpSequence = new Rlp[sequence.Count];
            for (int i = 0; i < sequence.Count; i++)
            {
                rlpSequence[i] = Encode(sequence[i]);
            }

            return Encode(rlpSequence);
        }

        public static Rlp Encode<T>(T[] sequence)
        {
            Rlp[] rlpSequence = new Rlp[sequence.Length];
            for (int i = 0; i < sequence.Length; i++)
            {
                rlpSequence[i] = Encode(sequence[i]);
            }

            return Encode(rlpSequence);
        }

        public static Rlp Encode(params Rlp[] sequence)
        {
            int contentLength = 0;
            for (int i = 0; i < sequence.Length; i++)
            {
                contentLength += sequence[i].Length;
            }

            byte[] content = new byte[contentLength];
            int offset = 0;
            for (int i = 0; i < sequence.Length; i++)
            {
                Buffer.BlockCopy(sequence[i].Bytes, 0, content, offset, sequence[i].Length);
                offset += sequence[i].Length;
            }

            if (contentLength < 56)
            {
                return new Rlp(Extensions.Bytes.Concat((byte)(192 + contentLength), content));
            }

            byte[] serializedLength = SerializeLength(contentLength);
            byte prefix = (byte)(247 + serializedLength.Length);
            return new Rlp(Extensions.Bytes.Concat(prefix, serializedLength, content));
        }

        public static Rlp Encode(params object[] sequence)
        {
            Rlp[] rlpSequence = new Rlp[sequence.Length];
            for (int i = 0; i < sequence.Length; i++)
            {
                rlpSequence[i] = Encode(sequence[i]);
            }

            return Encode(rlpSequence);
        }

        private static Rlp EncodeNumber(long item)
        {
            long value = item;

            // check test bytestring00 and zero - here is some inconsistency in tests
            if (value == 0L)
            {
                return OfEmptyByteArray;
            }

            if (value < 128L)
            {
                // ReSharper disable once PossibleInvalidCastException
                return new Rlp(Convert.ToByte(value));
            }

            if (value <= byte.MaxValue)
            {
                return Encode(new[] {Convert.ToByte(value)});
            }

            if (value <= short.MaxValue)
            {
                return Encode(((short)value).ToBigEndianByteArray());
            }

            return Encode(new BigInteger(value));
        }

        public static Rlp Encode(byte value)
        {
            return EncodeNumber(value);
        }

        public static Rlp Encode(long value)
        {
            return EncodeNumber(value);
        }

        public static Rlp Encode(ulong value)
        {
            return Encode(value.ToBigEndianByteArray());
        }

        public static Rlp Encode(short value)
        {
            return EncodeNumber(value);
        }

        public static Rlp Encode(ushort value)
        {
            return EncodeNumber(value);
        }

        public static Rlp Encode(int value)
        {
            return EncodeNumber(value);
        }

        public static Rlp Encode(uint value)
        {
            return EncodeNumber(value);
        }

        public static Rlp Encode(BigInteger bigInteger)
        {
            return bigInteger == 0 ? OfEmptyByteArray : Encode(bigInteger.ToBigEndianByteArray());
        }
        
        public static Rlp Encode(DecodedRlp decodedRlp)
        {
            return Encode(decodedRlp.IsSequence ? decodedRlp.Items.ToArray() : decodedRlp.SingleItem);
        }

        public static Rlp Encode(object item)
        {
            // TODO: review this nonsense later, can it be removed now?
            switch (item)
            {
                case byte singleByte:
                    if (singleByte == 0)
                    {
                        return OfEmptyByteArray;
                    }
                    else if (singleByte < 128)
                    {
                        return new Rlp(singleByte);
                    }
                    else
                    {
                        return Encode(new[] {singleByte});
                    }
                case short _:
                    return EncodeNumber((short)item);
                case int _:
                    return EncodeNumber((int)item);
                case ushort _:
                    return EncodeNumber((ushort)item);
                case uint _:
                    return EncodeNumber((uint)item);
                case long _:
                    return EncodeNumber((long)item);
                case null:
                    return OfEmptyByteArray;
                case BigInteger bigInt:
                    return Encode(bigInt);
                case string s:
                    return Encode(s);
                case Rlp rlp:
                    return rlp;
                case ulong ulongNumber:
                    return Encode(ulongNumber.ToBigEndianByteArray());
                case byte[] byteArray:
                    return Encode(byteArray);
                case Keccak keccak:
                    return Encode(keccak);
                case Keccak[] keccakArray:
                    return Encode(keccakArray);
                case object[] objects:
                    return Encode(objects);
                case Address address:
                    return Encode(address);
                case LogEntry logEntry:
                    return Encode(logEntry);
                case Block block:
                    return Encode(block);
                case BlockHeader header:
                    return Encode(header);
                case Bloom bloom:
                    return Encode(bloom);
                case DecodedRlp decoded:
                    return Encode(decoded);
            }

            throw new NotSupportedException($"RLP does not support items of type {item.GetType().Name}");
        }

        public static Rlp Encode(string s)
        {
            return Encode(System.Text.Encoding.ASCII.GetBytes(s));
        }

        public static Rlp Encode(byte[] input)
        {
            if (input.Length == 0)
            {
                return OfEmptyByteArray;
            }

            if (input.Length == 1 && input[0] < 128)
            {
                return new Rlp(input[0]);
            }

            if (input.Length < 56)
            {
                byte smallPrefix = (byte)(input.Length + 128);
                return new Rlp(Extensions.Bytes.Concat(smallPrefix, input));
            }

            byte[] serializedLength = SerializeLength(input.Length);
            byte prefix = (byte)(183 + serializedLength.Length);
            return new Rlp(Extensions.Bytes.Concat(prefix, serializedLength, input));
        }

        public static byte[] SerializeLength(long value)
        {
            const int maxResultLength = 8;
            byte[] bytes = new byte[maxResultLength];

            bytes[0] = (byte)(value >> 56);
            bytes[1] = (byte)(value >> 48);
            bytes[2] = (byte)(value >> 40);
            bytes[3] = (byte)(value >> 32);
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

        public static Rlp Encode(BlockHeader blockHeader, bool withMixHashAndNonce = true)
        {
            int numberOfElements = withMixHashAndNonce ? 15 : 13;
            Rlp[] elements = new Rlp[numberOfElements];
            elements[0] = Encode(blockHeader.ParentHash);
            elements[1] = Encode(blockHeader.OmmersHash);
            elements[2] = Encode(blockHeader.Beneficiary);
            elements[3] = Encode(blockHeader.StateRoot);
            elements[4] = Encode(blockHeader.TransactionsRoot);
            elements[5] = Encode(blockHeader.ReceiptsRoot);
            elements[6] = Encode(blockHeader.Bloom);
            elements[7] = Encode(blockHeader.Difficulty);
            elements[8] = Encode(blockHeader.Number);
            elements[9] = Encode(blockHeader.GasLimit);
            elements[10] = Encode(blockHeader.GasUsed);
            elements[11] = Encode(blockHeader.Timestamp);
            elements[12] = Encode(blockHeader.ExtraData);
            if (withMixHashAndNonce)
            {
                elements[13] = Encode(blockHeader.MixHash);
                elements[14] = Encode(blockHeader.Nonce);
            }

            return Encode(elements);
        }

        public static Rlp Encode(Block block)
        {
            return Encode(
                Encode(block.Header),
                Encode(block.Transactions),
                Encode(block.Ommers));
        }

        public static Rlp Encode(Bloom bloom)
        {
            if (bloom == null)
            {
                return OfEmptyByteArray;
            }
            
            byte[] result = new byte[259];
            result[0] = 185;
            result[1] = 1;
            result[2] = 0;
            Buffer.BlockCopy(bloom.Bytes, 0, result, 3, 256);
            return new Rlp(result);
        }

        public static Rlp Encode(LogEntry logEntry)
        {
            // TODO: can be slightly optimized in place
            return Encode(
                Encode(logEntry.LoggersAddress),
                Encode(logEntry.Topics),
                Encode(logEntry.Data));
        }

        public static Rlp Encode(Account account)
        {
            return Encode(
                Encode(account.Nonce),
                Encode(account.Balance),
                Encode(account.StorageRoot),
                Encode(account.CodeHash));
        }

        public static Rlp Encode(TransactionReceipt receipt, bool isEip658Enabled)
        {
            return Encode(
                isEip658Enabled ? Encode(receipt.StatusCode) : Encode(receipt.PostTransactionState),
                Encode(receipt.GasUsed),
                Encode(receipt.Bloom),
                Encode(receipt.Logs));
        }

        public static Rlp Encode(Transaction transaction, bool forSigning, bool isEip155Enabled = false, int chainId = 0)
        {
            Rlp[] sequence = new Rlp[forSigning && !(isEip155Enabled && chainId != 0) ? 6 : 9];
            sequence[0] = Encode(transaction.Nonce);
            sequence[1] = Encode(transaction.GasPrice);
            sequence[2] = Encode(transaction.GasLimit);
            sequence[3] = Encode(transaction.To);
            sequence[4] = Encode(transaction.Value);
            sequence[5] = Encode(transaction.To == null ? transaction.Init : transaction.Data);

            if (forSigning)
            {
                if (isEip155Enabled && chainId != 0)
                {
                    sequence[6] = Encode(chainId);
                    sequence[7] = OfEmptyByteArray;
                    sequence[8] = OfEmptyByteArray;
                }
            }
            else
            {
                sequence[6] = Encode(transaction.Signature?.V);
                sequence[7] = Encode(transaction.Signature?.R.WithoutLeadingZeros()); // TODO: consider storing R and S differently
                sequence[8] = Encode(transaction.Signature?.S.WithoutLeadingZeros()); // TODO: consider storing R and S differently
            }

            return Encode(sequence);
        }

        public static Rlp Encode(Transaction transaction)
        {
            return Encode(transaction, false);
        }

        public static Rlp Encode(Keccak keccak)
        {
            if (keccak == null)
            {
                return OfEmptyByteArray;
            }
            
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

        public class DecoderContext
        {
            public DecoderContext(byte[] data)
            {
                Data = data;
                MaxIndex = Data.Length;
            }

            public byte[] Data { get; }
            public int CurrentIndex { get; set; }
            public int MaxIndex { get; set; }

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
        }
    }
}