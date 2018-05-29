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
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Encoding
{
    /// <summary>
    ///     https://github.com/ethereum/wiki/wiki/RLP
    /// </summary>
    //[DebuggerStepThrough]
    public class Rlp
    {
        public static readonly Rlp OfEmptyByteArray = new Rlp(128);

        public static readonly Rlp OfEmptySequence = new Rlp(192);

        /// <summary>
        /// This is not encoding - just a creation of an RLP object, e.g. passing 192 would mean an RLP of an empty sequence.
        /// </summary>
        internal Rlp(byte singleByte)
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

        // TODO: discover decoders, use them for encoding as well
        private static readonly Dictionary<RuntimeTypeHandle, IRlpDecoder> Decoders =
            new Dictionary<RuntimeTypeHandle, IRlpDecoder>
            {
                [typeof(Account).TypeHandle] = new AccountDecoder(),
                [typeof(Block).TypeHandle] = new BlockDecoder(),
                [typeof(BlockHeader).TypeHandle] = new HeaderDecoder(),
                [typeof(BlockInfo).TypeHandle] = new BlockInfoDecoder(),
                [typeof(ChainLevelInfo).TypeHandle] = new ChainLevelDecoder(),
                [typeof(LogEntry).TypeHandle] = new LogEntryDecoder(),
                [typeof(NetworkNode).TypeHandle] = new NetworkNodeDecoder(),
                [typeof(Transaction).TypeHandle] = new TransactionDecoder(),
                [typeof(TransactionReceipt).TypeHandle] = new TransactionReceiptDecoder(),
            };

        public static T Decode<T>(Rlp oldRlp, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Decode<T>(oldRlp.Bytes.AsRlpContext(), rlpBehaviors);
        }

        public static T Decode<T>(byte[] bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Decode<T>(bytes.AsRlpContext(), rlpBehaviors);
        }

        public static T[] DecodeArray<T>(DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None) // TODO: move inside the context
        {
            if (Decoders.ContainsKey(typeof(T).TypeHandle))
            {
                IRlpDecoder<T> decoder = (IRlpDecoder<T>)Decoders[typeof(T).TypeHandle];
                int checkPosition = context.ReadSequenceLength() + context.Position;
                T[] result = new T[context.ReadNumberOfItemsRemaining(checkPosition)];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = decoder.Decode(context, rlpBehaviors);
                }

                return result;
            }

            throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
        }

        public static T Decode<T>(DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (Decoders.ContainsKey(typeof(T).TypeHandle))
            {
                return ((IRlpDecoder<T>)Decoders[typeof(T).TypeHandle]).Decode(context, rlpBehaviors);
            }

            throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
        }

        public static Rlp Encode<T>(T item, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (Decoders.ContainsKey(typeof(T).TypeHandle))
            {
                return ((IRlpDecoder<T>)Decoders[typeof(T).TypeHandle]).Encode(item, behaviors);
            }

            throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
        }

        public static Rlp Encode<T>(T[] items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (Decoders.ContainsKey(typeof(T).TypeHandle))
            {
                IRlpDecoder<T> decoder = (IRlpDecoder<T>)Decoders[typeof(T).TypeHandle];
                Rlp[] rlpSequence = new Rlp[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    rlpSequence[i] = decoder.Encode(items[i], behaviors);
                }

                return Encode(rlpSequence);
            }

            throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
        }

        public static Rlp Encode(Transaction transaction)
        {
            return Encode(transaction, false);
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
                // TODO: below obviously fails when Signature is null
                sequence[6] = transaction.Signature == null ? OfEmptyByteArray : Encode(transaction.Signature.V);
                sequence[7] = Encode(transaction.Signature?.R.WithoutLeadingZeros()); // TODO: consider storing R and S differently
                sequence[8] = Encode(transaction.Signature?.S.WithoutLeadingZeros()); // TODO: consider storing R and S differently
            }

            return Encode(sequence);
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
                return Encode(new[] { Convert.ToByte(value) });
            }

            if (value <= short.MaxValue)
            {
                return Encode(((short)value).ToBigEndianByteArray());
            }

            return Encode(new BigInteger(value));
        }

        public static Rlp Encode(bool value)
        {
            return value ? new Rlp(1) : OfEmptyByteArray;
        }

        public static Rlp Encode(byte value)
        {
            if (value == 0L)
            {
                return OfEmptyByteArray;
            }

            if (value < 128L)
            {
                return new Rlp(value);
            }

            return Encode(new[] { value });
        }

        public static Rlp Encode(long value)
        {
            return EncodeNumber(value);
        }

        // TODO: nonces only
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

        public static Rlp Encode(string s)
        {
            if (s == null)
            {
                return OfEmptyByteArray;
            }

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

        public static byte[] SerializeLength(int value)
        {
            if (value < 1 << 8)
            {
                return new[] { (byte)value };
            }

            if (value < 1 << 16)
            {
                return new[]
                {
                    (byte)(value >> 8),
                    (byte)value,
                };
            }

            if (value < 1 << 24)
            {
                return new[]
                {
                    (byte)(value >> 16),
                    (byte)(value >> 8),
                    (byte)value,
                };
            }

            return new[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            };
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

        public static Rlp Encode(Keccak[] sequence)
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

            byte[] serializedLength = null;
            byte prefix;
            if (contentLength < 56)
            {
                prefix = (byte)(192 + contentLength);
            }
            else
            {
                serializedLength = SerializeLength(contentLength);
                prefix = (byte)(247 + serializedLength.Length);
            }

            int lengthOfPrefixAndSerializedLength = 1 + (serializedLength?.Length ?? 0);
            byte[] allBytes = new byte[lengthOfPrefixAndSerializedLength + contentLength];
            allBytes[0] = prefix;
            int offset = 1;
            if (serializedLength != null)
            {
                Buffer.BlockCopy(serializedLength, 0, allBytes, offset, serializedLength.Length);
                offset += serializedLength.Length;
            }

            for (int i = 0; i < sequence.Length; i++)
            {
                Buffer.BlockCopy(sequence[i].Bytes, 0, allBytes, offset, sequence[i].Length);
                offset += sequence[i].Length;
            }

            return new Rlp(allBytes);
        }

        public class DecoderContext
        {
            public DecoderContext(byte[] data)
            {
                Data = data;
            }

            public byte[] Data { get; }

            public int Position { get; set; }

            public int Length => Data.Length;

            public bool IsSequenceNext()
            {
                return Data[Position] >= 192;
            }

            public int ReadNumberOfItemsRemaining(int? beforePosition = null)
            {
                int positionStored = Position;
                int numberOfItems = 0;
                while (Position < (beforePosition ?? Data.Length))
                {
                    int prefix = ReadByte();
                    if (prefix <= 128)
                    {
                    }
                    else if (prefix <= 183)
                    {
                        int length = prefix - 128;
                        Position += length;
                    }
                    else if (prefix < 192)
                    {
                        int lengthOfLength = prefix - 183;
                        int length = DeserializeLength(lengthOfLength);
                        if (length < 56)
                        {
                            throw new RlpException("Expected length greater or equal 56 and was {length}");
                        }

                        Position += length;
                    }
                    else
                    {
                        Position--;
                        int sequenceLength = ReadSequenceLength();
                        Position += sequenceLength;
                    }

                    numberOfItems++;
                }

                Position = positionStored;
                return numberOfItems;
            }

            public int ReadSequenceLength()
            {
                int prefix = ReadByte();
                if (prefix < 192)
                {
                    throw new RlpException($"Expected a sequence prefix to be in the range of <192, 255> and got {prefix}");
                }

                if (prefix <= 247)
                {
                    return prefix - 192;
                }

                int lengthOfConcatenationLength = prefix - 247;
                int concatenationLength = DeserializeLength(lengthOfConcatenationLength);
                if (concatenationLength < 56)
                {
                    throw new RlpException($"Expected length greater or equal 56 and got {concatenationLength}");
                }

                return concatenationLength;
            }

            private int DeserializeLength(int lengthOfLength)
            {
                int result;
                if (Data[Position] == 0)
                {
                    throw new RlpException("Length starts with 0");
                }

                if (lengthOfLength == 1)
                {
                    result = Data[Position];
                }
                else if (lengthOfLength == 2)
                {
                    result = Data[Position + 1] | (Data[Position] << 8);
                }
                else if (lengthOfLength == 3)
                {
                    result = Data[Position + 2] | (Data[Position + 1] << 8) | (Data[Position] << 16);
                }
                else if (lengthOfLength == 4)
                {
                    result = Data[Position + 3] | (Data[Position + 2] << 8) | (Data[Position + 1] << 16) | (Data[Position] << 24);
                }
                else
                {
                    // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                    throw new InvalidOperationException($"Invalid length of length = {lengthOfLength}");
                }

                Position += lengthOfLength;
                return result;
            }

            private byte ReadByte()
            {
                return Data[Position++];
            }

            private byte[] Read(int length)
            {
                byte[] bytes = new byte[length];
                Buffer.BlockCopy(Data, Position, bytes, 0, length);
                Position += length;
                return bytes;
            }

            public void Check(int nextCheck)
            {
                if (Position != nextCheck)
                {
                    throw new RlpException($"Data checkpoint failed. Expected {nextCheck} and is {Position}");
                }
            }

            public Keccak DecodeKeccak()
            {
                int prefix = ReadByte();
                if (prefix == 128)
                {
                    return null;
                }

                if (prefix != 128 + 32)
                {
                    throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Keccak)}");
                }

                byte[] buffer = Read(32);
                return new Keccak(buffer);
            }

            public Address DecodeAddress()
            {
                int prefix = ReadByte();
                if (prefix == 128)
                {
                    return null;
                }

                if (prefix != 128 + 20)
                {
                    throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Address)}");
                }

                byte[] buffer = Read(20);
                return new Address(buffer);
            }

            public BigInteger DecodeUBigInt()
            {
                byte[] bytes = DecodeByteArray();
                return bytes.ToUnsignedBigInteger();
            }

            public Bloom DecodeBloom()
            {
                // TODO: check first bytes
                byte[] bloomBytes = DecodeByteArray();
                if (bloomBytes.Length == 0)
                {
                    return null;
                }

                Bloom bloom = bloomBytes.Length == 256 ? new Bloom(bloomBytes.ToBigEndianBitArray2048()) : throw new InvalidOperationException("Incorrect bloom RLP");
                return bloom;
            }

            public byte[] ReadSequenceRlp()
            {
                int positionBefore = Position;
                int sequenceLength = (int)ReadSequenceLength();
                byte[] sequenceRlp = Data.Slice(positionBefore, Position - positionBefore + sequenceLength);
                Position += sequenceLength;
                return sequenceRlp;
            }

            public bool DecodeBool()
            {
                int prefix = ReadByte();
                if (prefix <= 128)
                {
                    return prefix == 1;
                }

                if (prefix <= 183)
                {
                    int length = prefix - 128;
                    if (length == 1 && Data[Position] < 128)
                    {
                        throw new RlpException($"Unexpected byte value {Data[Position]}");
                    }

                    bool result = Data[Position] == 1;
                    Position += length;
                    return result;
                }

                if (prefix < 192)
                {
                    int lengthOfLength = prefix - 183;
                    if (lengthOfLength > 4)
                    {
                        // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                        throw new RlpException("Expected length of lenth less or equal 4");
                    }

                    int length = DeserializeLength(lengthOfLength);
                    if (length < 56)
                    {
                        throw new RlpException("Expected length greater or equal 56 and was {length}");
                    }

                    bool result = Data[Position] == 1;
                    Position += length;
                    return result;
                }

                throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");
            }

            public T[] DecodeArray<T>(Func<DecoderContext, T> decodeItem)
            {
                int positionCheck = ReadSequenceLength() + Position;
                int count = ReadNumberOfItemsRemaining(positionCheck);
                T[] result = new T[count];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = decodeItem(this);
                }

                return result;
            }

            public string DecodeString()
            {
                byte[] bytes = DecodeByteArray();
                return System.Text.Encoding.UTF8.GetString(bytes);
            }

            public byte DecodeByte()
            {
                byte[] bytes = DecodeByteArray();
                return bytes.Length == 0 ? (byte)0 : bytes[0];
            }

            public int DecodeInt()
            {
                byte[] bytes = DecodeByteArray();
                return bytes.Length == 0 ? 0 : bytes.ToInt32();
            }

            public long DecodeLong()
            {
                byte[] bytes = DecodeByteArray();
                return bytes.Length == 0 ? 0L : bytes.ToInt64();
            }

            public byte[] DecodeByteArray()
            {
                int prefix = ReadByte();
                if (prefix == 0)
                {
                    return new byte[] { 0 };
                }

                if (prefix < 128)
                {
                    return new[] { (byte)prefix };
                }

                if (prefix == 128)
                {
                    return Extensions.Bytes.Empty;
                }

                if (prefix <= 183)
                {
                    int length = prefix - 128;
                    byte[] buffer = Read(length);
                    if (length == 1 && buffer[0] < 128)
                    {
                        throw new RlpException($"Unexpected byte value {buffer[0]}");
                    }

                    return buffer;
                }

                if (prefix < 192)
                {
                    int lengthOfLength = prefix - 183;
                    if (lengthOfLength > 4)
                    {
                        // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                        throw new RlpException("Expected length of lenth less or equal 4");
                    }

                    int length = DeserializeLength(lengthOfLength);
                    if (length < 56)
                    {
                        throw new RlpException("Expected length greater or equal 56 and was {length}");
                    }

                    byte[] buffer = Read(length);
                    return buffer;
                }

                throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");
            }
        }

        public bool Equals(Rlp other)
        {
            if (other == null)
            {
                return false;
            }

            return Extensions.Bytes.UnsafeCompare(Bytes, other.Bytes);
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToString(bool withZeroX)
        {
            return Hex.FromBytes(Bytes, withZeroX);
        }

        public int GetHashCode(Rlp obj)
        {
            return obj.Bytes.GetXxHashCode();
        }
    }
}