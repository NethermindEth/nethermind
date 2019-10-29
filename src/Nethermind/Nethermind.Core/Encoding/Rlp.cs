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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using Microsoft.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Encoding
{
    /// <summary>
    ///     https://github.com/ethereum/wiki/wiki/RLP
    /// </summary>
    //[DebuggerStepThrough]
    public class Rlp
    {
        public const int LengthOfKeccakRlp = 33;
        public const int LengthOfAddressRlp = 21;
        public const int LengthOfBloomRlp = 259;
        public const int LengthOfEmptyArrayRlp = 1;
        public const int LengthOfEmptySequenceRlp = 1;

        internal const int DebugMessageContentLength = 2048;

        public static readonly Rlp OfEmptyByteArray = new Rlp(128);

        public static readonly Rlp OfEmptySequence = new Rlp(192);
        
        internal static readonly Rlp OfEmptyTreeHash = Encode(Keccak.EmptyTreeHash.Bytes); // use bytes to avoid stack overflow
        
        internal static readonly Rlp OfEmptySequenceRlpHash = Encode(Keccak.OfAnEmptySequenceRlp.Bytes); // use bytes to avoid stack overflow
        
        internal static readonly Rlp OfEmptyStringHash = Encode(Keccak.OfAnEmptyString.Bytes); // use bytes to avoid stack overflow
        
        internal static readonly Rlp EmptyBloom = Encode(Bloom.Empty.Bytes); // use bytes to avoid stack overflow

        static Rlp()
        {
            RegisterDecoders(Assembly.GetAssembly(typeof(Rlp)));
        }

        /// <summary>
        /// This is not encoding - just a creation of an RLP object, e.g. passing 192 would mean an RLP of an empty sequence.
        /// </summary>
        private Rlp(byte singleByte)
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

        public static readonly Dictionary<Type, IRlpDecoder> Decoders = new Dictionary<Type, IRlpDecoder>();

        public static void RegisterDecoders(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsClass)
                {
                    continue;
                }

                var implementedInterfaces = type.GetInterfaces();
                foreach (var implementedInterface in implementedInterfaces)
                {
                    if (!implementedInterface.IsGenericType)
                    {
                        continue;
                    }

                    var interfaceGenericDefinition = implementedInterface.GetGenericTypeDefinition();
                    if (interfaceGenericDefinition == typeof(IRlpDecoder<>).GetGenericTypeDefinition())
                    {
                        var constructor = type.GetConstructor(Type.EmptyTypes);
                        if (constructor == null)
                        {
                            continue;
                        }

                        Decoders[implementedInterface.GenericTypeArguments[0]] = (IRlpDecoder) Activator.CreateInstance(type);
                    }
                }
            }
        }

        public static T Decode<T>(Rlp oldRlp, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Decode<T>(oldRlp.Bytes.AsRlpStream(), rlpBehaviors);
        }

        public static T Decode<T>(byte[] bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Decode<T>(bytes.AsRlpStream(), rlpBehaviors);
        }

        public static T[] DecodeArray<T>(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (Decoders.ContainsKey(typeof(T)))
            {
                IRlpDecoder<T> decoder = (IRlpDecoder<T>) Decoders[typeof(T)];
                int checkPosition = rlpStream.ReadSequenceLength() + rlpStream.Position;
                T[] result = new T[rlpStream.ReadNumberOfItemsRemaining(checkPosition)];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = decoder.Decode(rlpStream, rlpBehaviors);
                }

                return result;
            }

            throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
        }

        public static T Decode<T>(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (Decoders.ContainsKey(typeof(T)))
            {
                return ((IRlpDecoder<T>) Decoders[typeof(T)]).Decode(rlpStream, rlpBehaviors);
            }

            throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
        }

        public static Rlp Encode<T>(T item, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (item is Rlp rlp)
            {
                return Encode(new[] {rlp});
            }

            if (Decoders.ContainsKey(typeof(T)))
            {
                return ((IRlpDecoder<T>) Decoders[typeof(T)]).Encode(item, behaviors);
            }

            throw new RlpException($"{nameof(Rlp)} does not support encoding {typeof(T).Name}");
        }

        public static void Encode<T>(MemoryStream stream, T item, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (item is Rlp rlp)
            {
                stream.Write(Encode(new[] {rlp}).Bytes);
            }

            if (Decoders.ContainsKey(typeof(T)))
            {
                ((IRlpDecoder<T>) Decoders[typeof(T)]).Encode(stream, item, behaviors);
                return;
            }

            throw new RlpException($"{nameof(Rlp)} does not support encoding {typeof(T).Name}");
        }

        public static Rlp Encode<T>(T[] items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items == null)
            {
                return OfEmptySequence;
            }
            
            if (Decoders.ContainsKey(typeof(T)))
            {
                IRlpDecoder<T> decoder = (IRlpDecoder<T>) Decoders[typeof(T)];
                Rlp[] rlpSequence = new Rlp[items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    rlpSequence[i] = items[i] == null ? OfEmptySequence : decoder.Encode(items[i], behaviors);
                }

                return Encode(rlpSequence);
            }

            throw new RlpException($"{nameof(Rlp)} does not support encoding {typeof(T).Name}");
        }

        public static Rlp Encode(int[] integers)
        {
            Rlp[] rlpSequence = new Rlp[integers.Length];
            for (int i = 0; i < integers.Length; i++)
            {
                rlpSequence[i] = Encode(integers[i]);
            }

            return Encode(rlpSequence);
        }
        
        public static Rlp Encode(string[] strings)
        {
            Rlp[] rlpSequence = new Rlp[strings.Length];
            for (int i = 0; i < strings.Length; i++)
            {
                rlpSequence[i] = Encode(strings[i]);
            }

            return Encode(rlpSequence);
        }

        public static Rlp Encode(Transaction transaction)
        {
            return Encode(transaction, false);
        }

        public static Rlp Encode(
            Transaction transaction,
            bool forSigning,
            bool isEip155Enabled = false,
            int chainId = 0)
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
                sequence[7] =
                    Encode(transaction.Signature == null
                        ? null
                        : transaction.Signature.RAsSpan
                            .WithoutLeadingZeros()); // TODO: consider storing R and S differently
                sequence[8] =
                    Encode(transaction.Signature == null
                        ? null
                        : transaction.Signature.SAsSpan
                            .WithoutLeadingZeros()); // TODO: consider storing R and S differently
            }

            return Encode(sequence);
        }

        public static void Encode(MemoryStream stream, UInt256 value, int length = -1)
        {
            if (value.IsZero && length == -1)
            {
                stream.WriteByte(OfEmptyByteArray.Bytes[0]);
            }
            else
            {
                Span<byte> bytes = stackalloc byte[32];
                value.ToBigEndian(bytes);
                if (length != -1)
                {
                    Encode(stream, bytes.Slice(bytes.Length - length, length));
                }
                else
                {
                    Encode(stream, bytes.WithoutLeadingZeros());
                }
            }
        }

        /// <summary>
        /// Watch out - this only works for positive values
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="value"></param>
        public static void Encode(MemoryStream stream, int value)
        {
            Encode(stream, (long) value);
        }

        /// <summary>
        /// Watch out - this only works for positive values
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="value"></param>
        public static void Encode(MemoryStream stream, long value)
        {
            if (value == 0L)
            {
                stream.WriteByte(OfEmptyByteArray.Bytes[0]);
            }
            else
            {
                if (value < 128L)
                {
                    stream.WriteByte((byte) value);
                }
                else if (value <= byte.MaxValue)
                {
                    stream.WriteByte(129);
                    stream.WriteByte((byte) value);
                }
                else
                {
                    Encode(stream, (UInt256) value);
                }
            }
        }
        
        public static void Encode(MemoryStream stream, string value)
        {
            if (value == null)
            {
                stream.WriteByte(OfEmptyByteArray.Bytes[0]);
            }
            else
            {
                Encode(stream, System.Text.Encoding.ASCII.GetBytes(value));
            }
        }

        public static Rlp Encode(UInt256? value)
        {
            if (value.HasValue)
            {
                return Encode(value.Value);
            }
            else
            {
                return new Rlp(0);
            }
        }
        
        public static Rlp Encode(UInt256 value)
        {
            if (value.IsZero)
            {
                return OfEmptyByteArray;
            }

            Span<byte> bytes = stackalloc byte[32];
            value.ToBigEndian(bytes);
            return Encode(bytes.WithoutLeadingZeros());
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

            return Encode(new[] {value});
        }

        public static Rlp Encode(ushort value)
        {
            return Encode((long) value);
        }

        public static Rlp Encode(short value)
        {
            return Encode((long) value);
        }
        
        public static Rlp Encode(uint value)
        {
            return value == 0U ? OfEmptyByteArray : Encode((long) value);
        }

        public static Rlp Encode(int value)
        {
            if (value == 0)
            {
                return OfEmptyByteArray;
            }

            return value < 0 ? Encode(new BigInteger(value), 4) : Encode((long) value);
        }

        /// <summary>
        /// Special case for nonce
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static Rlp Encode(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            return Encode(bytes);
        }

        public static Rlp Encode(long value)
        {
            if (value == 0L)
            {
                return OfEmptyByteArray;
            }

            if (value > 0)
            {
                byte byte6 = (byte) (value >> 8);
                byte byte5 = (byte) (value >> 16);
                byte byte4 = (byte) (value >> 24);
                byte byte3 = (byte) (value >> 32);
                byte byte2 = (byte) (value >> 40);
                byte byte1 = (byte) (value >> 48);
                byte byte0 = (byte) (value >> 56);

                if (value < 256L * 256L * 256L * 256L * 256L * 256L * 256L)
                {
                    if (value < 256L * 256L * 256L * 256L * 256L * 256L)
                    {
                        if (value < 256L * 256L * 256L * 256L * 256L)
                        {
                            if (value < 256L * 256L * 256L * 256L)
                            {
                                if (value < 256 * 256 * 256)
                                {
                                    if (value < 256 * 256)
                                    {
                                        if (value < 128)
                                        {
                                            return new Rlp((byte) value);
                                        }

                                        return value < 256 ? new Rlp(new byte[] {129, (byte) value}) : new Rlp(new byte[] {130, byte6, (byte) value});
                                    }

                                    return new Rlp(new byte[] {131, byte5, byte6, (byte) value});
                                }

                                return new Rlp(new byte[] {132, byte4, byte5, byte6, (byte) value});
                            }

                            return new Rlp(new byte[] {133, byte3, byte4, byte5, byte6, (byte) value});
                        }

                        return new Rlp(new byte[] {134, byte2, byte3, byte4, byte5, byte6, (byte) value});
                    }

                    return new Rlp(new byte[] {135, byte1, byte2, byte3, byte4, byte5, byte6, (byte) value});
                }

                return new Rlp(new byte[] {136, byte0, byte1, byte2, byte3, byte4, byte5, byte6, (byte) value});
            }

            return Encode(new BigInteger(value), 8);
        }

        /// <summary>
        /// Used for nonce only - different behaviour (treated as a byte array)
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="value"></param>
        public static void Encode(MemoryStream stream, ulong value)
        {
            Encode(stream, value, 8);
        }

        public static Rlp Encode(BigInteger bigInteger, int outputLength = -1)
        {
            return bigInteger == 0 ? OfEmptyByteArray : Encode(bigInteger.ToBigEndianByteArray(outputLength));
        }

        public static Rlp Encode(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return OfEmptyByteArray;
            }

            return Encode(System.Text.Encoding.ASCII.GetBytes(s));
        }

        public static void Encode(MemoryStream stream, Span<byte> input)
        {
            if (input == null || input.Length == 0)
            {
                stream.Write(OfEmptyByteArray.Bytes);
            }
            else if (input.Length == 1 && input[0] < 128)
            {
                stream.WriteByte(input[0]);
            }
            else if (input.Length < 56)
            {
                byte smallPrefix = (byte) (input.Length + 128);
                stream.WriteByte(smallPrefix);
                stream.Write(input);
            }
            else
            {
                int lengthOfLength = LengthOfLength(input.Length);
                byte prefix = (byte) (183 + lengthOfLength);
                stream.WriteByte(prefix);
                SerializeLength(stream, input.Length);
                stream.Write(input);
            }
        }

        public static void Encode(MemoryStream stream, byte[] data)
        {
            Encode(stream, data.AsSpan());
        }

        public static int Encode(Span<byte> buffer, int position, byte[] input)
        {
            if (input == null || input.Length == 0)
            {
                buffer[position++] = OfEmptyByteArray.Bytes[0];
                return position;
            }

            if (input.Length == 1 && input[0] < 128)
            {
                buffer[position++] = input[0];
                return position;
            }

            if (input.Length < 56)
            {
                byte smallPrefix = (byte) (input.Length + 128);
                buffer[position++] = smallPrefix;
            }
            else
            {
                int lengthOfLength = LengthOfLength(input.Length);
                byte prefix = (byte) (183 + lengthOfLength);
                buffer[position++] = prefix;
                SerializeLength(buffer, position, input.Length);
            }

            input.AsSpan().CopyTo(buffer.Slice(position, input.Length));
            position += input.Length;

            return position;
        }

        public static Rlp Encode(Span<byte> input)
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
                byte smallPrefix = (byte) (input.Length + 128);
                byte[] rlpResult = new byte[input.Length + 1];
                rlpResult[0] = smallPrefix;
                input.CopyTo(rlpResult.AsSpan(1));
                return new Rlp(rlpResult);
            }
            else
            {
                byte[] serializedLength = SerializeLength(input.Length);
                byte prefix = (byte) (183 + serializedLength.Length);
                byte[] rlpResult = new byte[1 + serializedLength.Length + input.Length];
                rlpResult[0] = prefix;
                serializedLength.CopyTo(rlpResult.AsSpan(1));
                input.CopyTo(rlpResult.AsSpan(1 + serializedLength.Length));
                return new Rlp(rlpResult);
            }
        }

        public static Rlp Encode(byte[] input)
        {
            return Encode(input.AsSpan());
        }

        public static void SerializeLength(MemoryStream stream, int value)
        {
            if (value < 1 << 8)
            {
                stream.WriteByte((byte) value);
            }
            else if (value < 1 << 16)
            {
                stream.WriteByte((byte) (value >> 8));
                stream.WriteByte((byte) value);
            }
            else if (value < 1 << 24)
            {
                stream.WriteByte((byte) (value >> 16));
                stream.WriteByte((byte) (value >> 8));
                stream.WriteByte((byte) value);
            }
            else
            {
                stream.WriteByte((byte) (value >> 24));
                stream.WriteByte((byte) (value >> 16));
                stream.WriteByte((byte) (value >> 8));
                stream.WriteByte((byte) value);
            }
        }

        public static int SerializeLength(Span<byte> buffer, int position, int value)
        {
            if (value < 1 << 8)
            {
                buffer[position] = (byte) value;
                return position + 1;
            }

            if (value < 1 << 16)
            {
                buffer[position] = (byte) (value >> 8);
                buffer[position + 1] = ((byte) value);
                return position + 2;
            }

            if (value < 1 << 24)
            {
                buffer[position] = (byte) (value >> 16);
                buffer[position + 1] = ((byte) (value >> 8));
                buffer[position + 2] = ((byte) value);
                return position + 3;
            }

            buffer[position] = (byte) (value >> 24);
            buffer[position + 1] = (byte) (value >> 16);
            buffer[position + 2] = (byte) (value >> 8);
            buffer[position + 3] = (byte) value;
            return position + 4;
        }

        internal static int LengthOfLength(int value)
        {
            if (value < 1 << 8)
            {
                return 1;
            }

            if (value < 1 << 16)
            {
                return 2;
            }

            if (value < 1 << 24)
            {
                return 3;
            }

            return 4;
        }

        public static byte[] SerializeLength(int value)
        {
            if (value < 1 << 8)
            {
                return new[] {(byte) value};
            }

            if (value < 1 << 16)
            {
                return new[]
                {
                    (byte) (value >> 8),
                    (byte) value,
                };
            }

            if (value < 1 << 24)
            {
                return new[]
                {
                    (byte) (value >> 16),
                    (byte) (value >> 8),
                    (byte) value,
                };
            }

            return new[]
            {
                (byte) (value >> 24),
                (byte) (value >> 16),
                (byte) (value >> 8),
                (byte) value
            };
        }

        public static Rlp Encode(Bloom bloom)
        {
            if (bloom == null)
            {
                return OfEmptyByteArray;
            }

            if (ReferenceEquals(bloom, Bloom.Empty))
            {
                return EmptyBloom;
            }

            byte[] result = new byte[259];
            result[0] = 185;
            result[1] = 1;
            result[2] = 0;
            Buffer.BlockCopy(bloom.Bytes, 0, result, 3, 256);
            return new Rlp(result);
        }

        public static void Encode(MemoryStream stream, Keccak keccak)
        {
            if (keccak == null)
            {
                stream.WriteByte(OfEmptyByteArray.Bytes[0]);
            }
            else if (ReferenceEquals(keccak, Keccak.EmptyTreeHash))
            {
                stream.Write(OfEmptyTreeHash.Bytes);
            }
            else if (ReferenceEquals(keccak, Keccak.OfAnEmptyString))
            {
                stream.Write(OfEmptyStringHash.Bytes);
            }
            else
            {
                stream.WriteByte(160);
                stream.Write(keccak.Bytes);
            }
        }

        public static void Encode(MemoryStream stream, Address address)
        {
            if (address == null)
            {
                stream.WriteByte(OfEmptyByteArray.Bytes[0]);
            }
            else
            {
                stream.WriteByte(148);
                stream.Write(address.Bytes);
            }
        }

        public static void Encode(MemoryStream stream, Bloom bloom)
        {
            if (ReferenceEquals(bloom, Bloom.Empty))
            {
                stream.WriteByte(185);
                stream.WriteByte(1);
                stream.WriteByte(0);
                stream.Position += 256;
            }
            else if (bloom == null)
            {
                stream.WriteByte(OfEmptyByteArray.Bytes[0]);
            }
            else
            {
                stream.WriteByte(185);
                stream.WriteByte(1);
                stream.WriteByte(0);
                stream.Write(bloom.Bytes);
            }
        }

        public static Rlp Encode(Keccak keccak)
        {
            if (keccak == null)
            {
                return OfEmptyByteArray;
            }

            if (ReferenceEquals(keccak, Keccak.EmptyTreeHash))
            {
                return OfEmptyTreeHash;
            }

            if (ReferenceEquals(keccak, Keccak.OfAnEmptyString))
            {
                return OfEmptyStringHash;
            }

            byte[] result = new byte[LengthOfKeccakRlp];
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
            Buffer.BlockCopy(address.Bytes, 0, result, 1, 20);
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

        public static int GetSequenceRlpLength(int contentLength)
        {
            int totalLength = contentLength + 1;
            if (contentLength >= 56)
            {
                totalLength += LengthOfLength(contentLength);
            }

            return totalLength;
        }

        public static void StartSequence(MemoryStream stream, int contentLength)
        {
            byte prefix;
            if (contentLength < 56)
            {
                prefix = (byte) (192 + contentLength);
                stream.WriteByte(prefix);
            }
            else
            {
                prefix = (byte) (247 + LengthOfLength(contentLength));
                stream.WriteByte(prefix);
                SerializeLength(stream, contentLength);
            }
        }

        public static int StartSequence(byte[] buffer, int position, int sequenceLength)
        {
            byte prefix;
            int beforeLength = position + 1;
            int afterLength = position + 1;
            if (sequenceLength < 56)
            {
                prefix = (byte) (192 + sequenceLength);
            }
            else
            {
                afterLength = SerializeLength(buffer, beforeLength, sequenceLength);
                prefix = (byte) (247 + afterLength - beforeLength);
            }

            buffer[position] = prefix;
            return afterLength;
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
                prefix = (byte) (192 + contentLength);
            }
            else
            {
                serializedLength = SerializeLength(contentLength);
                prefix = (byte) (247 + serializedLength.Length);
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

        public ref struct ValueDecoderContext
        {
            public ValueDecoderContext(Span<byte> data)
            {
                Data = data;
                Position = 0;
            }

            public Span<byte> Data { get; }

            public bool IsEmpty => Data.IsEmpty;
            
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

            public void SkipLength()
            {
                Position += PeekPrefixAndContentLength().PrefixLength;
            }

            public int PeekNextRlpLength()
            {
                (int a, int b) = PeekPrefixAndContentLength();
                return a + b;
            }

            public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
            {
                int memorizedPosition = Position;
                (int prefixLength, int contentLengt) result;
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

                Position = memorizedPosition;
                return result;
            }

            public int ReadSequenceLength()
            {
                int prefix = ReadByte();
                if (prefix < 192)
                {
                    throw new RlpException($"Expected a sequence prefix to be in the range of <192, 255> and got {prefix} at position {Position} in the message of length {Data.Length} starting with {Data.Slice(0, Math.Min(DebugMessageContentLength, Data.Length)).ToHexString()}");
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
                    result = Data[Position + 3] | (Data[Position + 2] << 8) | (Data[Position + 1] << 16) |
                             (Data[Position] << 24);
                }
                else
                {
                    // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                    throw new InvalidOperationException($"Invalid length of length = {lengthOfLength}");
                }

                Position += lengthOfLength;
                return result;
            }

            public byte ReadByte()
            {
                return Data[Position++];
            }

            public Span<byte> Read(int length)
            {
                Span<byte> data = Data.Slice(Position, length);
                Position += length;
                return data;
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
                    throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Keccak)} at position {Position} in the message of length {Data.Length} starting with {Data.Slice(0, Math.Min(DebugMessageContentLength, Data.Length)).ToHexString()}");
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

            public Address DecodeAddress()
            {
                int prefix = ReadByte();
                if (prefix == 128)
                {
                    return null;
                }

                if (prefix != 128 + 20)
                {
                    throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Keccak)} at position {Position} in the message of length {Data.Length} starting with {Data.Slice(0, Math.Min(DebugMessageContentLength, Data.Length)).ToHexString()}");
                }

                byte[] buffer = Read(20).ToArray();
                return new Address(buffer);
            }

            public UInt256 DecodeUInt256()
            {
                Span<byte> byteSpan = DecodeByteArraySpan();
                if (byteSpan.Length > 32)
                {
                    throw new ArgumentException();
                }

                UInt256.CreateFromBigEndian(out UInt256 result, byteSpan);
                return result;
            }
            
            public UInt256? DecodeNullableUInt256()
            {
                if (Data[Position] == 0)
                {
                    Position++;
                    return null;
                }

                return DecodeUInt256();
            }

            public BigInteger DecodeUBigInt()
            {
                Span<byte> bytes = DecodeByteArraySpan();
                return bytes.ToUnsignedBigInteger();
            }

            public Bloom DecodeBloom()
            {
                Span<byte> bloomBytes;

                // tks: not sure why but some nodes send us Blooms in a sequence form
                // https://github.com/NethermindEth/nethermind/issues/113
                if (Data[Position] == 249)
                {
                    Position += 5; // tks: skip 249 1 2 129 127 and read 256 bytes 
                    bloomBytes = Read(256);
                }
                else
                {
                    bloomBytes = DecodeByteArraySpan();
                    if (bloomBytes.Length == 0)
                    {
                        return null;
                    }
                }

                if (bloomBytes.Length != 256)
                {
                    throw new InvalidOperationException("Incorrect bloom RLP");
                }

                if (bloomBytes.SequenceEqual(Extensions.Bytes.Zero256))
                {
                    return Bloom.Empty;
                }

                return new Bloom(bloomBytes.ToBigEndianBitArray2048());
            }

            public Span<byte> PeekNextItem()
            {
                int length = PeekNextRlpLength();
                Span<byte> item = Read(length);
                Position -= item.Length;
                return item;
            }

            public bool IsNextItemNull()
            {
                return Data[Position] == 192;
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
                        throw new RlpException("Expected length of length less or equal 4");
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

                throw new RlpException($"Unexpected prefix of {prefix} when decoding a byte array at position {Position} in the message of length {Data.Length} starting with {Data.Slice(0, Math.Min(DebugMessageContentLength, Data.Length)).ToHexString()}");
            }

            public string DecodeString()
            {
                Span<byte> bytes = DecodeByteArraySpan();
                return System.Text.Encoding.UTF8.GetString(bytes);
            }

            public byte DecodeByte()
            {
                Span<byte> bytes = DecodeByteArraySpan();
                return bytes.Length == 0 ? (byte)0 :
                    bytes.Length == 1 ? bytes[0] == (byte)128
                        ? (byte)0
                        : bytes[0]
                    : bytes[1];
            }

            public int DecodeInt()
            {
                int prefix = ReadByte();
                if (prefix < 128)
                {
                    return prefix;
                }

                if (prefix == 128)
                {
                    return 0;
                }

                int length = prefix - 128;
                if (length > 4)
                {
                    throw new RlpException($"Unexpected length of int value: {length}");
                }

                int result = 0;
                for (int i = 4; i > 0; i--)
                {
                    result = result << 8;
                    if (i <= length)
                    {
                        result = result | Data[Position + length - i];
                    }
                }
                
                Position += length;

                return result;
            }

            public byte[] DecodeByteArray()
            {
                return DecodeByteArraySpan().ToArray();
            }

            public Span<byte> DecodeByteArraySpan()
            {
                int prefix = ReadByte();
                if (prefix == 0)
                {
                    return new byte[] {0};
                }

                if (prefix < 128)
                {
                    return new[] {(byte) prefix};
                }

                if (prefix == 128)
                {
                    return Extensions.Bytes.Empty;
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
                        throw new RlpException("Expected length of lenth less or equal 4");
                    }

                    int length = DeserializeLength(lengthOfLength);
                    if (length < 56)
                    {
                        throw new RlpException("Expected length greater or equal 56 and was {length}");
                    }

                    return Read(length);
                }

                throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");
            }

            public void SkipItem()
            {
                (int prefix, int content) = PeekPrefixAndContentLength();
                Position += prefix + content;
            }

            public void Reset()
            {
                Position = 0;
            }
        }

        public override bool Equals(object other)
        {
            return Equals(other as Rlp);
        }

        public override int GetHashCode()
        {
            return Bytes != null ? Bytes.GetSimplifiedHashCode() : 0;
        }

        public bool Equals(Rlp other)
        {
            return null != other && Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToString(bool withZeroX)
        {
            return Bytes.ToHexString(withZeroX);
        }

        public static int LengthOf(UInt256 item)
        {
            if (item < 128UL)
            {
                return 1;
            }

            Span<byte> bytes = stackalloc byte[32];
            item.ToBigEndian(bytes);
            int length = bytes.WithoutLeadingZeros().Length;
            return length + 1;
        }

        public static int LengthOf(ulong _)
        {
            return 9;
        }

        public static int LengthOf(long value)
        {
            // everything has a length prefix
            if (value < 0)
            {
                return 9;
            }

            if (value < 256L * 256L * 256L * 256L * 256L * 256L * 256L)
            {
                if (value < 256L * 256L * 256L * 256L * 256L * 256L)
                {
                    if (value < 256L * 256L * 256L * 256L * 256L)
                    {
                        if (value < 256L * 256L * 256L * 256L)
                        {
                            if (value < 256 * 256 * 256)
                            {
                                if (value < 256 * 256)
                                {
                                    if (value < 128)
                                    {
                                        return 1;
                                    }

                                    return value < 256 ? 2 : 3;
                                }

                                return 4;
                            }

                            return 5;
                        }

                        return 6;
                    }

                    return 7;
                }

                return 8;
            }

            return 9;
        }

        public static int LengthOf(int value)
        {
            // everything has a length prefix
            if (value < 0)
            {
                return 9; // we cast it to long now
            }

            if (value < 256 * 256 * 256)
            {
                if (value < 256 * 256)
                {
                    if (value < 128)
                    {
                        return 1;
                    }

                    return value < 256 ? 2 : 3;
                }

                return 4;
            }

            return 5;
        }

        public static int LengthOf(Keccak item)
        {
            return item == null ? 1 : 33;
        }

        public static int LengthOf(Address item)
        {
            return item == null ? 1 : 21;
        }

        public static int LengthOf(Bloom bloom)
        {
            return bloom == null ? 1 : 259;
        }

        public static int LengthOfSequence(int contentLength)
        {
            if (contentLength < 56)
            {
                return 1 + contentLength;
            }

            return 1 + LengthOfLength(contentLength) + contentLength;
        }

        public static int LengthOf(byte[] array)
        {
            return LengthOf(array.AsSpan());
        }

        public static int LengthOf(Span<byte> array)
        {
            if (array == null || array.Length == 0)
            {
                return 1;
            }

            if (array.Length == 1 && array[0] < 128)
            {
                return 1;
            }

            if (array.Length < 56)
            {
                return array.Length + 1;
            }

            return LengthOfLength(array.Length) + 1 + array.Length;
        }

        public static int LengthOf(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 1;
            }
            
            var spanString = value.AsSpan();
            
            if (spanString.Length == 1 && spanString[0] < 128)
            {
                return 1;
            }
            
            if (spanString.Length < 56)
            {
                return spanString.Length + 1;
            }
            
            return LengthOfLength(spanString.Length) + 1 + spanString.Length;
        }

        public static int LengthOf(byte value)
        {
            return 1;
        }
        
        public static int LengthOf(bool value)
        {
            return 1;
        }
        
        public static int LengthOf(LogEntry item) 
        {
            if (Decoders.ContainsKey(typeof(LogEntry)))
            {
                return ((IRlpDecoder<LogEntry>) Decoders[typeof(LogEntry)]).GetLength(item, RlpBehaviors.None);
            }

            throw new RlpException($"{nameof(Rlp)} does not support length of {typeof(LogEntry).Name}");
        }
    }
}