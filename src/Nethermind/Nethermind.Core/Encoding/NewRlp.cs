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
    public static class NewRlp
    {
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

        public static T Decode<T>(DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (Decoders.ContainsKey(typeof(T).TypeHandle))
            {
                return ((IRlpDecoder<T>)Decoders[typeof(T).TypeHandle]).Decode(context, rlpBehaviors);
            }

            throw new RlpException($"{nameof(NewRlp)} does not support decoding {typeof(T).Name}");
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

            public int ReadNumberOfItemsRemaining()
            {
                int positionStored = Position;
                int numberOfItems = 0;
                while (Position < Data.Length)
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
                        int sequenceLength = (int)ReadSequenceLength();
                        Position += sequenceLength;
                    }

                    numberOfItems++;
                }

                Position = positionStored;
                return numberOfItems;
            }

            public long ReadSequenceLength()
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

            public int DeserializeLength(int lengthOfLength)
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

            public void Check(long nextCheck)
            {
                if (Position != nextCheck)
                {
                    throw new RlpException($"Data checkpoint failed. Expected {nextCheck} and is {Position}");
                }
            }

            public Keccak ReadKeccak()
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

            public Address ReadAddress()
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

            public BigInteger ReadUBigInt()
            {
                byte[] bytes = ReadByteArray();
                return bytes.ToUnsignedBigInteger();
            }

            public Bloom ReadBloom()
            {
                // TODO: check first bytes
                byte[] bloomBytes = ReadByteArray();
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

//            return bytes.Length != 0 && bytes[0] == 1;

            public bool ReadBool()
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

            public byte[] ReadByteArray()
            {
                int prefix = ReadByte();
                if (prefix == 0)
                {
                    return new byte[] {0};
                }

                if (prefix < 128)
                {
                    return new[] {(byte)prefix};
                }

                if (prefix == 128)
                {
                    return Bytes.Empty;
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

                    long length = DeserializeLength(lengthOfLength);
                    if (length < 56)
                    {
                        throw new RlpException("Expected length greater or equal 56 and was {length}");
                    }

                    byte[] buffer = Read((int)length);
                    return buffer;
                }

                throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");
            }
        }
    }
}