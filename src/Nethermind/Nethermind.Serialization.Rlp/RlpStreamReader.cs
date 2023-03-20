// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    public struct RlpStreamReader
    {
        public long MemorySize => MemorySizes.SmallObjectOverhead
                                  + MemorySizes.Align(MemorySizes.ArrayOverhead + Length)
                                  + MemorySizes.Align(sizeof(int));

        public RlpStreamReader(byte[] data) => Data = data;

        private string Description =>
            Data?.Slice(0, Math.Min(Rlp.DebugMessageContentLength, Length)).ToHexString() ?? "0x";

        public bool IsNull => Data is null;

        public byte[]? Data { get; }

        public int Position { get; set; }

        public int Length => Data!.Length;

        public int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue)
        {
            int positionStored = Position;
            int numberOfItems = 0;
            while (Position < (beforePosition ?? Length))
            {
                int prefix = ReadByte();
                if (prefix <= 128)
                {
                }
                else if (prefix <= 183)
                {
                    int length = prefix - 128;
                    SkipBytes(length);
                }
                else if (prefix < 192)
                {
                    int lengthOfLength = prefix - 183;
                    int length = DeserializeLength(lengthOfLength);
                    if (length < 56)
                    {
                        throw new RlpException("Expected length greater or equal 56 and was {length}");
                    }

                    SkipBytes(length);
                }
                else
                {
                    Position--;
                    int sequenceLength = ReadSequenceLength();
                    SkipBytes(sequenceLength);
                }

                numberOfItems++;
                if (numberOfItems >= maxSearch)
                {
                    break;
                }
            }

            Position = positionStored;
            return numberOfItems;
        }

        public void SkipLength()
        {
            SkipBytes(PeekPrefixAndContentLength().PrefixLength);
        }

        public int PeekNextRlpLength()
        {
            (int a, int b) = PeekPrefixAndContentLength();
            return a + b;
        }

        public (int PrefixLength, int ContentLength) ReadPrefixAndContentLength()
        {
            (int prefixLength, int contentLength) result;
            int prefix = ReadByte();
            if (prefix <= 128)
            {
                result = (0, 1);
            }
            else if (prefix <= 183)
            {
                result = (1, prefix - 128);
            }
            else if (prefix < 192)
            {
                int lengthOfLength = prefix - 183;
                if (lengthOfLength > 4)
                {
                    // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                    throw new RlpException("Expected length of length less or equal 4");
                }

                int length = DeserializeLength(lengthOfLength);
                if (length < 56)
                {
                    throw new RlpException("Expected length greater or equal 56 and was {length}");
                }

                result = (lengthOfLength + 1, length);
            }
            else if (prefix <= 247)
            {
                result = (1, prefix - 192);
            }
            else
            {
                int lengthOfContentLength = prefix - 247;
                int contentLength = DeserializeLength(lengthOfContentLength);
                if (contentLength < 56)
                {
                    throw new RlpException($"Expected length greater or equal 56 and got {contentLength}");
                }


                result = (lengthOfContentLength + 1, contentLength);
            }

            return result;
        }

        public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
        {
            int memorizedPosition = Position;
            (int PrefixLength, int ContentLength) result = ReadPrefixAndContentLength();

            Position = memorizedPosition;
            return result;
        }

        public int ReadSequenceLength()
        {
            int prefix = ReadByte();
            if (prefix < 192)
            {
                throw new RlpException(
                    $"Expected a sequence prefix to be in the range of <192, 255> and got {prefix} at position {Position} in the message of length {Length} starting with {Description}");
            }

            if (prefix <= 247)
            {
                return prefix - 192;
            }

            int lengthOfContentLength = prefix - 247;
            int contentLength = DeserializeLength(lengthOfContentLength);
            if (contentLength < 56)
            {
                throw new RlpException($"Expected length greater or equal 56 and got {contentLength}");
            }

            return contentLength;
        }

        private int DeserializeLength(int lengthOfLength)
        {
            if (lengthOfLength == 0 || (uint)lengthOfLength > 4)
            {
                ThrowArgumentOutOfRangeException(lengthOfLength);
            }

            // Will use Unsafe.ReadUnaligned as we know the length of the span is same
            // as what we asked for and then explicitly check lengths, so can skip the
            // additional bounds checking from BinaryPrimitives.ReadUInt16BigEndian etc
            ref byte firstElement = ref MemoryMarshal.GetReference(Read(lengthOfLength));

            int result = firstElement;
            if (result == 0)
            {
                ThrowInvalidData();
            }

            if (lengthOfLength == 1)
            {
                // Already read above
                // result = span[0];
            }
            else if (lengthOfLength == 2)
            {
                if (BitConverter.IsLittleEndian)
                {
                    result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ushort>(ref firstElement));
                }
                else
                {
                    result = Unsafe.ReadUnaligned<ushort>(ref firstElement);
                }
            }
            else if (lengthOfLength == 3)
            {
                if (BitConverter.IsLittleEndian)
                {
                    result = BinaryPrimitives.ReverseEndianness(
                                 Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1)))
                             | (result << 16);
                }
                else
                {
                    result = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref firstElement, 1))
                             | (result << 16);
                }
            }
            else
            {
                if (BitConverter.IsLittleEndian)
                {
                    result = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<int>(ref firstElement));
                }
                else
                {
                    result = Unsafe.ReadUnaligned<int>(ref firstElement);
                }
            }

            return result;

            [DoesNotReturn]
            static void ThrowInvalidData()
            {
                throw new RlpException("Length starts with 0");
            }

            [DoesNotReturn]
            static void ThrowArgumentOutOfRangeException(int lengthOfLength)
            {
                throw new InvalidOperationException($"Invalid length of length = {lengthOfLength}");
            }
        }

        public byte ReadByte()
        {
            return Data![Position++];
        }

        private void SkipBytes(int length)
        {
            Position += length;
        }

        public Span<byte> Read(int length)
        {
            Span<byte> data = Data.AsSpan(Position, length);
            Position += length;
            return data;
        }

        public Keccak? DecodeKeccak()
        {
            int prefix = ReadByte();
            if (prefix == 128)
            {
                return null;
            }

            if (prefix != 128 + 32)
            {
                throw new RlpException(
                    $"Unexpected prefix of {prefix} when decoding {nameof(Keccak)} at position {Position} in the message of length {Length} starting with {Description}");
            }

            Span<byte> keccakSpan = Read(32);
            if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                return Keccak.OfAnEmptyString;
            }

            if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
            {
                return Keccak.EmptyTreeHash;
            }

            return new Keccak(keccakSpan.ToArray());
        }

        public Span<byte> PeekNextItem()
        {
            int length = PeekNextRlpLength();
            return Peek(length);
        }

        public Span<byte> Peek(int length)
        {
            Span<byte> item = Read(length);
            Position -= item.Length;
            return item;
        }

        public byte[] DecodeByteArray()
        {
            return DecodeByteArraySpan().ToArray();
        }

        public ReadOnlySpan<byte> DecodeByteArraySpan()
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
                return Array.Empty<byte>();
            }

            if (prefix <= 183)
            {
                int length = prefix - 128;
                Span<byte> buffer = Read(length);
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
                    throw new RlpException("Expected length of length less or equal 4");
                }

                int length = DeserializeLength(lengthOfLength);
                if (length < 56)
                {
                    throw new RlpException($"Expected length greater or equal 56 and was {length}");
                }

                return Read(length);
            }

            throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");
        }

        public void NullSafeSkipItem()
        {
            if (IsNull is false)
            {
                SkipItem();
            }
        }

        public void SkipItem()
        {
            (int prefix, int content) = PeekPrefixAndContentLength();
            SkipBytes(prefix + content);
        }

        public void Reset()
        {
            Position = 0;
        }

        public override string ToString()
        {
            return $"[{nameof(RlpStream)}|{Position}/{Length}]";
        }
    }
}
