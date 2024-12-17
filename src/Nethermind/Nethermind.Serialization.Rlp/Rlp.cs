// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    /// <summary>
    ///     https://github.com/ethereum/wiki/wiki/RLP
    ///
    ///     Note: Prefer RlpStream to encode instead, which does not create a new byte array on each call.
    /// </summary>
    public class Rlp
    {
        public const int LengthOfKeccakRlp = 33;

        public const int LengthOfAddressRlp = 21;

        internal const int DebugMessageContentLength = 2048;

        public const byte EmptyArrayByte = 128;

        public const byte NullObjectByte = 192; // use bytes to avoid stack overflow

        internal const int LengthOfNull = 1;

        public static readonly Rlp OfEmptyByteArray = new(EmptyArrayByte);

        public static readonly Rlp OfEmptySequence = new(NullObjectByte);

        internal static readonly Rlp OfEmptyTreeHash = Encode(Keccak.EmptyTreeHash.Bytes); // use bytes to avoid stack overflow

        internal static readonly Rlp OfEmptyStringHash = Encode(Keccak.OfAnEmptyString.Bytes); // use bytes to avoid stack overflow

        internal static readonly Rlp EmptyBloom = Encode(Bloom.Empty.Bytes);
        static Rlp()
        {
            RegisterDecoders(Assembly.GetAssembly(typeof(Rlp)));
        }

        /// <summary>
        /// This is not encoding - just a creation of an RLP object, e.g. passing 192 would mean an RLP of an empty sequence.
        /// </summary>
        private Rlp(byte singleByte)
        {
            Bytes = new[] { singleByte };
        }

        public Rlp(byte[] bytes)
        {
            Bytes = bytes ?? throw new RlpException("RLP cannot be initialized with null bytes");
        }

        public long MemorySize => /* this */ MemorySizes.SmallObjectOverhead +
                                            MemorySizes.Align(MemorySizes.ArrayOverhead + Bytes.Length);

        public byte[] Bytes { get; }

        public byte this[int index] => Bytes[index];

        public int Length => Bytes.Length;

        private static readonly Dictionary<RlpDecoderKey, IRlpDecoder> _decoderBuilder = new();
        private static FrozenDictionary<RlpDecoderKey, IRlpDecoder>? _decoders;
        public static FrozenDictionary<RlpDecoderKey, IRlpDecoder> Decoders => _decoders ??= _decoderBuilder.ToFrozenDictionary();

        public static void RegisterDecoder(RlpDecoderKey key, IRlpDecoder decoder)
        {
            _decoderBuilder[key] = decoder;
            // Mark FrozenDictionary as null to force re-creation
            _decoders = null;
        }

        public static void RegisterDecoders(Assembly assembly, bool canOverrideExistingDecoders = false)
        {
            foreach (Type? type in assembly.GetExportedTypes())
            {
                if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                if (type.GetCustomAttribute<SkipGlobalRegistration>() is not null)
                {
                    continue;
                }

                Type[]? implementedInterfaces = type.GetInterfaces();
                foreach (Type? implementedInterface in implementedInterfaces)
                {
                    if (!implementedInterface.IsGenericType)
                    {
                        continue;
                    }

                    Type? interfaceGenericDefinition = implementedInterface.GetGenericTypeDefinition();
                    if (interfaceGenericDefinition == typeof(IRlpDecoder<>).GetGenericTypeDefinition())
                    {
                        bool isSetForAnyAttribute = false;
                        IRlpDecoder? instance = null;

                        foreach (DecoderAttribute rlpDecoderAttr in type.GetCustomAttributes<DecoderAttribute>())
                        {
                            RlpDecoderKey key = new(implementedInterface.GenericTypeArguments[0], rlpDecoderAttr.Key);
                            AddEncoder(key);

                            isSetForAnyAttribute = true;
                        }

                        if (!isSetForAnyAttribute)
                        {
                            AddEncoder(new(implementedInterface.GenericTypeArguments[0]));
                        }

                        void AddEncoder(RlpDecoderKey key)
                        {
                            if (!_decoderBuilder.TryGetValue(key, out IRlpDecoder? value) || canOverrideExistingDecoders)
                            {
                                try
                                {
                                    _decoderBuilder[key] = instance ??= (IRlpDecoder)(type.GetConstructor(Type.EmptyTypes) is not null ?
                                        Activator.CreateInstance(type) :
                                        Activator.CreateInstance(type, BindingFlags.CreateInstance | BindingFlags.OptionalParamBinding, null, [Type.Missing], null));
                                }
                                catch (Exception)
                                {
                                    throw new ArgumentException($"Unable to set decoder for {key}, because {type} decoder has no suitable constructor.");
                                }
                            }
                            else
                            {
                                throw new InvalidOperationException($"Unable to override decoder for {key}, because the following decoder is already set: {value}.");
                            }
                        }
                    }
                }
            }

            // Mark FrozenDictionary as null to force re-creation
            _decoders = null;
        }

        public static T Decode<T>(Rlp oldRlp, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Decode<T>(oldRlp.Bytes.AsRlpStream(), rlpBehaviors);
        }

        public static T Decode<T>(byte[]? bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Decode<T>(bytes.AsRlpStream(), rlpBehaviors);
        }

        public static T Decode<T>(Span<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var valueContext = bytes.AsRlpValueContext();
            return Decode<T>(ref valueContext, rlpBehaviors);
        }

        public static T[] DecodeArray<T>(RlpStream rlpStream, IRlpStreamDecoder<T>? rlpDecoder, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int checkPosition = rlpStream.ReadSequenceLength() + rlpStream.Position;
            T[] result = new T[rlpStream.PeekNumberOfItemsRemaining(checkPosition)];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = rlpDecoder.Decode(rlpStream, rlpBehaviors);
            }

            return result;
        }

        public static ArrayPoolList<T> DecodeArrayPool<T>(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            IRlpStreamDecoder<T>? rlpDecoder = GetStreamDecoder<T>();
            if (rlpDecoder is not null)
            {
                return DecodeArrayPool(rlpStream, rlpDecoder, rlpBehaviors);
            }

            throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
        }

        public static ArrayPoolList<T> DecodeArrayPool<T>(RlpStream rlpStream, IRlpStreamDecoder<T>? rlpDecoder, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int checkPosition = rlpStream.ReadSequenceLength() + rlpStream.Position;
            int length = rlpStream.PeekNumberOfItemsRemaining(checkPosition);
            ArrayPoolList<T> result = new ArrayPoolList<T>(length);
            for (int i = 0; i < length; i++)
            {
                result.Add(rlpDecoder.Decode(rlpStream, rlpBehaviors));
            }

            return result;
        }

        internal static byte[] ByteSpanToArray(ReadOnlySpan<byte> span)
        {
            if (span.Length == 0)
            {
                return [];
            }

            if (span.Length == 1)
            {
                int value = span[0];
                var arrays = RlpStream.SingleByteArrays;
                if ((uint)value < (uint)arrays.Length)
                {
                    return arrays[value];
                }
            }

            return span.ToArray();
        }

        internal static ArrayPoolList<byte> ByteSpanToArrayPool(ReadOnlySpan<byte> span)
        {
            if (span.Length == 0)
            {
                return ArrayPoolList<byte>.Empty();
            }

            if (span.Length == 1)
            {
                int value = span[0];
                var arrays = RlpStream.SingleByteArrays;
                if ((uint)value < (uint)arrays.Length)
                {
                    return arrays[value].ToPooledList();
                }
            }

            return span.ToPooledList();
        }

        public static IRlpValueDecoder<T>? GetValueDecoder<T>(string key = RlpDecoderKey.Default) => Decoders.TryGetValue(new(typeof(T), key), out IRlpDecoder value) ? value as IRlpValueDecoder<T> : null;
        public static IRlpStreamDecoder<T>? GetStreamDecoder<T>(string key = RlpDecoderKey.Default) => Decoders.TryGetValue(new(typeof(T), key), out IRlpDecoder value) ? value as IRlpStreamDecoder<T> : null;
        public static IRlpObjectDecoder<T> GetObjectDecoder<T>(string key = RlpDecoderKey.Default) => Decoders.GetValueOrDefault(new(typeof(T), key)) as IRlpObjectDecoder<T> ?? throw new RlpException($"{nameof(Rlp)} does not support encoding {typeof(T).Name}");

        public static T Decode<T>(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            IRlpStreamDecoder<T>? rlpDecoder = GetStreamDecoder<T>();
            bool shouldCheckStream = rlpStream.Position == 0 && (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes;
            int length = rlpStream.Length;
            T? result = rlpDecoder is not null ? rlpDecoder.Decode(rlpStream, rlpBehaviors) : throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
            if (shouldCheckStream)
                rlpStream.Check(length);
            return result;
        }

        public static T Decode<T>(ref RlpValueStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            IRlpValueDecoder<T>? rlpDecoder = GetValueDecoder<T>();
            bool shouldCheckStream = rlpStream.Position == 0 && (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes;
            int length = rlpStream.Length;
            T? result = rlpDecoder is not null ? rlpDecoder.Decode(ref rlpStream, rlpBehaviors) : throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
            if (shouldCheckStream)
                rlpStream.Check(length);
            return result;
        }

        public static Rlp Encode(Account item, RlpBehaviors behaviors = RlpBehaviors.None)
            => AccountDecoder.Instance.Encode(item, behaviors);

        public static Rlp Encode(LogEntry item, RlpBehaviors behaviors = RlpBehaviors.None)
            => LogEntryDecoder.Instance.Encode(item, behaviors);

        public static Rlp Encode<T>(T item, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (item is Rlp rlp)
            {
                RlpStream stream = new(LengthOfSequence(rlp.Length));
                return new(stream.Data.ToArray());
            }

            IRlpStreamDecoder<T>? rlpStreamDecoder = GetStreamDecoder<T>();
            if (rlpStreamDecoder is not null)
            {
                int totalLength = rlpStreamDecoder.GetLength(item, behaviors);
                RlpStream stream = new(totalLength);
                rlpStreamDecoder.Encode(stream, item, behaviors);
                return new Rlp(stream.Data.ToArray());
            }

            return GetObjectDecoder<T>().Encode(item, behaviors);
        }

        public static Rlp Encode<T>(T[]? items, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            if (items is null)
            {
                return OfEmptySequence;
            }

            IRlpStreamDecoder<T>? rlpStreamDecoder = GetStreamDecoder<T>();
            if (rlpStreamDecoder is not null)
            {
                int totalLength = rlpStreamDecoder.GetLength(items, behaviors);
                RlpStream stream = new(totalLength);
                rlpStreamDecoder.Encode(stream, items, behaviors);
                return new Rlp(stream.Data.ToArray());
            }

            return GetObjectDecoder<T>().Encode(items, behaviors);
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

        public static Rlp Encode(Transaction transaction)
        {
            return Encode(transaction, false);
        }

        public static Rlp Encode(Transaction transaction, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0) =>
            TxDecoder.Instance.EncodeTx(transaction, RlpBehaviors.SkipTypedWrapping, forSigning, isEip155Enabled, chainId);

        public static Rlp Encode(int value)
        {
            if (value == 0)
            {
                return OfEmptyByteArray;
            }

            return value < 0 ? Encode(new BigInteger(value), 4) : Encode((long)value);
        }

        public static Rlp Encode(long value)
        {
            return value switch
            {
                < 0 => Encode(new BigInteger(value), 8),
                0L => OfEmptyByteArray,
                < 0x80 => new((byte)value),
                < 0x100 => new(new byte[] { 129, (byte)value }),
                < 0x1_0000 => new(new byte[] { 130, (byte)(value >> 8), (byte)value }),
                < 0x100_0000 => new(new byte[] { 131, (byte)(value >> 16), (byte)(value >> 8), (byte)value }),
                < 0x1_0000_0000 => new(new byte[] { 132, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }),
                < 0x100_0000_0000 => new(new byte[] { 133, (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }),
                < 0x1_0000_0000_0000 => new(new byte[] { 134, (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }),
                < 0x100_0000_0000_0000 => new(new byte[] { 135, (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }),
                _ => new(new byte[] { 136, (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }),
            };
        }

        public static Rlp Encode(BigInteger bigInteger, int outputLength = -1)
        {
            return bigInteger == 0 ? OfEmptyByteArray : Encode(bigInteger.ToBigEndianByteArray(outputLength));
        }

        public static Rlp Encode(in UInt256 value, int length = -1)
        {
            if (value.IsZero && length == -1)
            {
                return OfEmptyByteArray;
            }
            else
            {
                Span<byte> bytes = stackalloc byte[32];
                value.ToBigEndian(bytes);
                return Encode(length != -1 ? bytes.Slice(bytes.Length - length, length) : bytes.WithoutLeadingZeros());
            }
        }

        public static int Encode(Span<byte> buffer, int position, ReadOnlySpan<byte> input)
        {
            if (input.Length == 1 && input[0] < 128)
            {
                buffer[position++] = input[0];
                return position;
            }

            if (input.Length < 56)
            {
                byte smallPrefix = (byte)(input.Length + 128);
                buffer[position++] = smallPrefix;
            }
            else
            {
                int lengthOfLength = LengthOfLength(input.Length);
                byte prefix = (byte)(183 + lengthOfLength);
                buffer[position++] = prefix;
                SerializeLength(buffer, position, input.Length);
            }

            input.CopyTo(buffer.Slice(position, input.Length));
            position += input.Length;

            return position;
        }

        public static int Encode(Span<byte> buffer, int position, Hash256 hash)
        {
            Debug.Assert(hash is not null);
            var newPosition = position + LengthOfKeccakRlp;
            if ((uint)newPosition > (uint)buffer.Length)
            {
                ThrowArgumentOutOfRangeException();
            }

            Unsafe.Add(ref MemoryMarshal.GetReference(buffer), (nuint)position) = 160;
            Unsafe.As<byte, ValueHash256>(ref Unsafe.Add(ref MemoryMarshal.GetReference(buffer), (nuint)position + 1)) = hash.ValueHash256;
            return newPosition;

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowArgumentOutOfRangeException()
            {
                throw new ArgumentOutOfRangeException(nameof(buffer));
            }
        }

        public static Rlp Encode(ReadOnlySpan<byte> input)
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
                byte[] rlpResult = new byte[input.Length + 1];
                rlpResult[0] = smallPrefix;
                input.CopyTo(rlpResult.AsSpan(1));
                return new Rlp(rlpResult);
            }
            else
            {
                byte[] serializedLength = SerializeLength(input.Length);
                byte prefix = (byte)(183 + serializedLength.Length);
                byte[] rlpResult = new byte[1 + serializedLength.Length + input.Length];
                rlpResult[0] = prefix;
                serializedLength.CopyTo(rlpResult.AsSpan(1));
                input.CopyTo(rlpResult.AsSpan(1 + serializedLength.Length));
                return new Rlp(rlpResult);
            }
        }

        public static Rlp Encode(byte[]? input)
        {
            return input is null ? OfEmptyByteArray : Encode(input.AsSpan());
        }

        public static int SerializeLength(Span<byte> buffer, int position, int value)
        {
            if (value < 1 << 8)
            {
                buffer[position] = (byte)value;
                return position + 1;
            }

            if (value < 1 << 16)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer[position..], (ushort)value);
                return position + 2;
            }

            if (value < 1 << 24)
            {
                buffer[position] = (byte)(value >> 16);
                buffer[position + 1] = ((byte)(value >> 8));
                buffer[position + 2] = ((byte)value);
                return position + 3;
            }

            BinaryPrimitives.WriteInt32BigEndian(buffer[position..], value);
            return position + 4;
        }

        public static int LengthOfLength(int value)
        {
            int bits = 32 - BitOperations.LeadingZeroCount((uint)value | 1);
            return (bits + 7) / 8;
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

        public static Rlp Encode(Hash256? keccak)
        {
            if (keccak is null)
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
            Unsafe.As<byte, ValueHash256>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(result), 1)) = keccak.ValueHash256;
            return new Rlp(result);
        }

        public static int StartSequence(Span<byte> buffer, int position, int sequenceLength)
        {
            byte prefix;
            int beforeLength = position + 1;
            int afterLength = position + 1;
            if (sequenceLength < 56)
            {
                prefix = (byte)(192 + sequenceLength);
            }
            else
            {
                afterLength = SerializeLength(buffer, beforeLength, sequenceLength);
                prefix = (byte)(247 + afterLength - beforeLength);
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
            if (serializedLength is not null)
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

        public override bool Equals(object? other)
        {
            return Equals(other as Rlp);
        }

        public override int GetHashCode() => new ReadOnlySpan<byte>(Bytes).FastHash();

        public bool Equals(Rlp? other)
        {
            return other is not null && Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);
        }

        public override string ToString()
        {
            return ToString(true);
        }

        public string ToString(bool withZeroX)
        {
            return Bytes.ToHexString(withZeroX);
        }

        public static int LengthOf(UInt256? item)
        {
            return item is null ? LengthOfNull : LengthOf(item.Value);
        }

        public static int LengthOf(in UInt256 item)
        {
            ulong value;
            int size;
            if (item.u3 > 0)
            {
                value = item.u3;
                size = 1 + sizeof(ulong) * 4;
            }
            else if (item.u2 > 0)
            {
                value = item.u2;
                size = 1 + sizeof(ulong) * 3;
            }
            else if (item.u1 > 0)
            {
                value = item.u1;
                size = 1 + sizeof(ulong) * 2;
            }
            else if (item.u0 < 128)
            {
                return 1;
            }
            else
            {
                value = item.u0;
                size = 1 + sizeof(ulong);
            }

            return size - (BitOperations.LeadingZeroCount(value) / 8);
        }

        public static int LengthOf(byte[][]? arrays)
        {
            int contentLength = 0;
            if (arrays is null)
            {
                return LengthOfNull;
            }

            foreach (byte[] item in arrays)
            {
                contentLength += Rlp.LengthOf(item);
            }
            return LengthOfSequence(contentLength);
        }

        public static int LengthOfNonce(ulong _)
        {
            return 9;
        }

        public static int LengthOf(long value)
        {
            if ((ulong)value < 128)
            {
                return 1;
            }
            else
            {
                // everything has a length prefix
                return 1 + sizeof(ulong) - (BitOperations.LeadingZeroCount((ulong)value) / 8);
            }
        }

        public static int LengthOf(int value) => LengthOf((long)value);

        public static int LengthOf(Hash256? item)
        {
            return item is null ? 1 : 33;
        }

        public static int LengthOf(in ValueHash256? item)
        {
            return item is null ? 1 : 33;
        }

        public static int LengthOf(Hash256[] keccaks, bool includeLengthOfSequenceStart = false)
        {
            int value = keccaks?.Length * LengthOfKeccakRlp ?? 0;

            if (includeLengthOfSequenceStart)
            {
                value = LengthOfSequence(value);
            }

            return value;
        }

        public static int LengthOf(ValueHash256[] keccaks, bool includeLengthOfSequenceStart = false)
        {
            int value = keccaks?.Length * LengthOfKeccakRlp ?? 0;

            if (includeLengthOfSequenceStart)
            {
                value = LengthOfSequence(value);
            }

            return value;
        }

        public static int LengthOf(IReadOnlyList<Hash256> keccaks, bool includeLengthOfSequenceStart = false)
        {
            int value = keccaks?.Count * LengthOfKeccakRlp ?? 0;

            if (includeLengthOfSequenceStart)
            {
                value = LengthOfSequence(value);
            }

            return value;
        }

        public static int LengthOf(IReadOnlyList<ValueHash256> keccaks, bool includeLengthOfSequenceStart = false)
        {
            int value = keccaks?.Count * LengthOfKeccakRlp ?? 0;

            if (includeLengthOfSequenceStart)
            {
                value = LengthOfSequence(value);
            }

            return value;
        }

        public static int LengthOf(Address? item)
        {
            return item is null ? 1 : 21;
        }

        public static int LengthOf(Bloom? bloom)
        {
            return bloom is null ? 1 : 259;
        }

        public static int LengthOfSequence(int contentLength)
        {
            if (contentLength < 56)
            {
                return 1 + contentLength;
            }

            return 1 + LengthOfLength(contentLength) + contentLength;
        }

        public static int LengthOf(byte[]? array)
        {
            return LengthOf(array.AsSpan());
        }

        public static int LengthOf(Memory<byte>? memory)
        {
            return LengthOf(memory.GetValueOrDefault().Span);
        }

        public static int LengthOf(IReadOnlyList<byte> array)
        {
            if (array.Count == 0)
            {
                return 1;
            }

            return LengthOfByteString(array.Count, array[0]);
        }

        public static int LengthOf(ReadOnlySpan<byte> array)
        {
            if (array.Length == 0)
            {
                return 1;
            }

            return LengthOfByteString(array.Length, array[0]);
        }

        // Assumes that length is greater then 0
        public static int LengthOfByteString(int length, byte firstByte)
        {
            if (length == 0)
            {
                return 1;
            }

            if (length == 1 && firstByte < 128)
            {
                return 1;
            }

            if (length < 56)
            {
                return length + 1;
            }

            return LengthOfLength(length) + 1 + length;
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
            return 1 + value / 128;
        }

        public static int LengthOf(bool value)
        {
            return 1;
        }

        public static int LengthOf(LogEntry item)
        {
            return LogEntryDecoder.Instance.GetLength(item, RlpBehaviors.None);
        }

        public static int LengthOf(BlockInfo item)
        {
            return BlockInfoDecoder.Instance.GetLength(item, RlpBehaviors.None);
        }

        [AttributeUsage(AttributeTargets.Class)]
        public class SkipGlobalRegistration : Attribute
        {
        }

        /// <summary>
        /// Optional attribute for RLP decoders.
        /// </summary>
        /// <param name="key">Optional custom key that helps to have more than one decoder for the given type.</param>
        [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
        public sealed class DecoderAttribute(string key = RlpDecoderKey.Default) : Attribute
        {
            public string Key { get; } = key;
        }
    }

    public readonly struct RlpDecoderKey(Type type, string key = RlpDecoderKey.Default) : IEquatable<RlpDecoderKey>
    {
        public const string Default = "default";
        public const string Storage = "storage";
        public const string LegacyStorage = "legacy-storage";
        public const string Trie = "trie";

        private readonly Type _type = type;
        private readonly string _key = key;
        public Type Type => _type;
        public string Key => _key;

        public static implicit operator Type(RlpDecoderKey key) => key._type;
        public static implicit operator RlpDecoderKey(Type key) => new(key);

        public bool Equals(RlpDecoderKey other) => _type.Equals(other._type) && _key.Equals(other._key);

        public override int GetHashCode() => HashCode.Combine(_type, _key);

        public override bool Equals(object obj) => obj is RlpDecoderKey key && Equals(key);

        public static bool operator ==(RlpDecoderKey left, RlpDecoderKey right) => left.Equals(right);

        public static bool operator !=(RlpDecoderKey left, RlpDecoderKey right) => !(left == right);

        public override string ToString()
        {
            return $"({Type.Name},{Key})";
        }
    }
}
