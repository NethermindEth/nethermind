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
using System.Text;
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
            return Decode<T>(rlpStream, rlpDecoder, rlpBehaviors);
        }
        public static T Decode<T>(RlpStream rlpStream, IRlpStreamDecoder<T> rlpDecoder, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ArgumentNullException.ThrowIfNull(rlpDecoder, nameof(rlpDecoder));
            bool shouldCheckStream = rlpStream.Position == 0 && (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes;
            int length = rlpStream.Length;
            T? result = rlpDecoder.Decode(rlpStream, rlpBehaviors);
            if (shouldCheckStream)
                rlpStream.Check(length);
            return result;
        }

        public static T Decode<T>(ref ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            IRlpValueDecoder<T>? rlpDecoder = GetValueDecoder<T>();
            bool shouldCheckStream = decoderContext.Position == 0 && (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes;
            int length = decoderContext.Length;
            T? result = rlpDecoder is not null ? rlpDecoder.Decode(ref decoderContext, rlpBehaviors) : throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
            if (shouldCheckStream)
                decoderContext.Check(length);
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
                position += SerializeLength(input.Length, buffer[position..]);
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

        [SkipLocalsInit]
        public static Rlp Encode(ReadOnlySpan<byte> input)
        {
            // Handle special cases first
            int length = input.Length;
            if (length == 0)
            {
                return OfEmptyByteArray;
            }

            // If it's a single byte less than 128, it encodes to itself.
            if (length == 1 && input[0] < 128)
            {
                return new Rlp(input[0]);
            }

            // For lengths < 56, the encoding is one byte of prefix + the data
            if (length < 56)
            {
                // Allocate exactly what we need: 1 prefix byte + input length
                byte[] rlpResult = GC.AllocateUninitializedArray<byte>(1 + length);
                // First byte is 0x80 + length
                rlpResult[0] = (byte)(0x80 + length);
                // Copy input after the prefix
                input.CopyTo(rlpResult.AsSpan(1));
                return new Rlp(rlpResult);
            }
            else
            {
                int lengthOfLength = LengthOfLength(length);
                // Total size = 1 prefix byte + lengthOfLength + data length
                int totalSize = 1 + lengthOfLength + length;
                byte[] rlpResult = GC.AllocateUninitializedArray<byte>(totalSize);
                // Prefix: 0xb7 (183) + number of bytes in length
                rlpResult[0] = (byte)(0xb7 + lengthOfLength);
                SerializeLength(length, rlpResult.AsSpan(1, lengthOfLength));
                // Finally copy the actual input
                input.CopyTo(rlpResult.AsSpan(1 + lengthOfLength));
                return new Rlp(rlpResult);
            }
        }

        public static Rlp Encode(byte[]? input)
        {
            return input is null ? OfEmptyByteArray : Encode(input.AsSpan());
        }

        private static int SerializeLength(int value, Span<byte> destination)
        {
            // We assume 0 <= value <= int.MaxValue
            if (value < (1 << 8))
            {
                destination[0] = (byte)value;
                return 1;
            }

            if (value < (1 << 16))
            {
                BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)value);
                return 2;
            }

            if (value < (1 << 24))
            {
                destination[2] = (byte)value;
                destination[1] = (byte)(value >> 8);
                destination[0] = (byte)(value >> 16);
                return 3;
            }

            // Otherwise, 4 bytes
            BinaryPrimitives.WriteInt32BigEndian(destination, value);
            return 4;
        }

        public static int LengthOfLength(int value)
        {
            int bits = 32 - BitOperations.LeadingZeroCount((uint)value | 1);
            return (bits + 7) / 8;
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
                afterLength = beforeLength + SerializeLength(sequenceLength, buffer[beforeLength..]);
                prefix = (byte)(247 + afterLength - beforeLength);
            }

            buffer[position] = prefix;
            return afterLength;
        }

        [SkipLocalsInit]
        public static Rlp Encode(params Rlp[] sequence)
        {
            int contentLength = 0;
            for (int i = 0; i < sequence.Length; i++)
            {
                contentLength += sequence[i].Length;
            }

            int lengthOfLength = 0;
            byte prefix;

            if (contentLength < 56)
            {
                // Single-byte prefix: 0xc0 + length
                prefix = (byte)(0xc0 + contentLength);
            }
            else
            {
                lengthOfLength = LengthOfLength(contentLength);
                // Multi-byte prefix: 0xf7 + lengthOfLength
                prefix = (byte)(0xf7 + lengthOfLength);
            }

            // Allocate the final buffer exactly once (prefix + optional length + content).
            int totalSize = 1 + lengthOfLength + contentLength;
            byte[] allBytes = GC.AllocateUninitializedArray<byte>(totalSize);

            // Write prefix and length.
            allBytes[0] = prefix;
            int offset = 1;
            if (lengthOfLength > 0)
            {
                SerializeLength(contentLength, allBytes.AsSpan(offset, lengthOfLength));
                offset += lengthOfLength;
            }

            // Copy the content from all Rlp items.
            for (int i = 0; i < sequence.Length; i++)
            {
                Buffer.BlockCopy(sequence[i].Bytes, 0, allBytes, offset, sequence[i].Length);
                offset += sequence[i].Length;
            }

            // Wrap in Rlp and return.
            return new Rlp(allBytes);
        }

        public ref struct ValueDecoderContext
        {
            public ValueDecoderContext(scoped in ReadOnlySpan<byte> data)
            {
                Data = data;
                Position = 0;
            }

            public ValueDecoderContext(Memory<byte> memory, bool sliceMemory = false)
            {
                Memory = memory;
                Data = memory.Span;
                Position = 0;

                // Slice memory is turned off by default. Because if you are not careful and being explicit about it,
                // you can end up with a memory leak.
                _sliceMemory = sliceMemory;
            }

            public Memory<byte>? Memory { get; }

            private bool _sliceMemory = false;

            public ReadOnlySpan<byte> Data { get; }

            public readonly bool IsEmpty => Data.IsEmpty;

            public int Position { get; set; }

            public readonly int Length => Data.Length;

            public readonly bool ShouldSliceMemory => _sliceMemory;

            public readonly bool IsSequenceNext()
            {
                return Data[Position] >= 192;
            }

            public int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue)
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
                Position += PeekPrefixAndContentLength().PrefixLength;
            }

            public int PeekNextRlpLength()
            {
                (int a, int b) = PeekPrefixAndContentLength();
                return a + b;
            }

            public ReadOnlySpan<byte> Peek(int length)
            {
                ReadOnlySpan<byte> item = Read(length);
                Position -= item.Length;
                return item;
            }

            public (int PrefixLength, int ContentLength) ReadPrefixAndContentLength()
            {
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
                    throw new RlpException($"Expected a sequence prefix to be in the range of <192, 255> and got {prefix} at position {Position} in the message of length {Data.Length} starting with {Data[..Math.Min(DebugMessageContentLength, Data.Length)].ToHexString()}");
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

            public ReadOnlySpan<byte> Read(int length)
            {
                ReadOnlySpan<byte> data = Data.Slice(Position, length);
                Position += length;
                return data;
            }

            public Memory<byte> ReadMemory(int length)
            {
                if (_sliceMemory && Memory.HasValue) return ReadSlicedMemory(length);
                return Read(length).ToArray();
            }

            private Memory<byte> ReadSlicedMemory(int length)
            {
                Memory<byte> data = Memory.Value.Slice(Position, length);
                Position += length;
                return data;
            }

            public readonly void Check(int nextCheck)
            {
                if (Position != nextCheck)
                {
                    throw new RlpException($"Data checkpoint failed. Expected {nextCheck} and is {Position}");
                }
            }

            // This class was introduce to reduce allocations when deserializing receipts. In order to deserialize receipts we first try to deserialize it in new format and then in old format.
            // If someone didn't do migration this will result in excessive allocations and GC of the not needed strings.
            private class DecodeKeccakRlpException : RlpException
            {
                private readonly int _prefix;
                private readonly int _position;
                private readonly int _dataLength;
                private string? _message;

                public DecodeKeccakRlpException(string message, Exception inner) : base(message, inner)
                {
                }

                public DecodeKeccakRlpException(string message) : base(message)
                {
                }

                public DecodeKeccakRlpException(in int prefix, in int position, in int dataLength) : this(string.Empty)
                {
                    _prefix = prefix;
                    _position = position;
                    _dataLength = dataLength;
                }

                public override string Message => _message ??= ConstructMessage();

                private string ConstructMessage() => $"Unexpected prefix of {_prefix} when decoding {nameof(Hash256)} at position {_position} in the message of length {_dataLength}.";
            }

            public Hash256? DecodeKeccak()
            {
                int prefix = ReadByte();
                if (prefix == 128)
                {
                    return null;
                }

                if (prefix != 128 + 32)
                {
                    throw new DecodeKeccakRlpException(prefix, Position, Data.Length);
                }

                ReadOnlySpan<byte> keccakSpan = Read(32);
                if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
                {
                    return Keccak.OfAnEmptyString;
                }

                if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
                {
                    return Keccak.EmptyTreeHash;
                }

                return new Hash256(keccakSpan);
            }

            public ValueHash256? DecodeValueKeccak()
            {
                int prefix = ReadByte();
                if (prefix == 128)
                {
                    return null;
                }

                if (prefix != 128 + 32)
                {
                    throw new DecodeKeccakRlpException(prefix, Position, Data.Length);
                }

                ReadOnlySpan<byte> keccakSpan = Read(32);
                if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
                {
                    return Keccak.OfAnEmptyString.ValueHash256;
                }

                if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
                {
                    return Keccak.EmptyTreeHash.ValueHash256;
                }

                return new ValueHash256(keccakSpan);
            }

            public Hash256? DecodeZeroPrefixKeccak()
            {
                int prefix = PeekByte();
                if (prefix == 128)
                {
                    ReadByte();
                    return null;
                }

                ReadOnlySpan<byte> theSpan = DecodeByteArraySpan();
                byte[] keccakByte = new byte[32];
                theSpan.CopyTo(keccakByte.AsSpan(32 - theSpan.Length));
                return new Hash256(keccakByte);
            }

            public void DecodeKeccakStructRef(out Hash256StructRef keccak)
            {
                int prefix = ReadByte();
                if (prefix == 128)
                {
                    keccak = new Hash256StructRef(Keccak.Zero.Bytes);
                }
                else if (prefix != 128 + 32)
                {
                    throw new DecodeKeccakRlpException(prefix, Position, Data.Length);
                }
                else
                {
                    ReadOnlySpan<byte> keccakSpan = Read(32);
                    if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
                    {
                        keccak = new Hash256StructRef(Keccak.OfAnEmptyString.Bytes);
                    }
                    else if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
                    {
                        keccak = new Hash256StructRef(Keccak.EmptyTreeHash.Bytes);
                    }
                    else
                    {
                        keccak = new Hash256StructRef(keccakSpan);
                    }
                }
            }

            public void DecodeZeroPrefixedKeccakStructRef(out Hash256StructRef keccak, Span<byte> buffer)
            {
                int prefix = PeekByte();
                if (prefix == 128)
                {
                    ReadByte();
                    keccak = new Hash256StructRef(Keccak.Zero.Bytes);
                }
                else if (prefix > 128 + 32)
                {
                    ReadByte();
                    throw new DecodeKeccakRlpException(prefix, Position, Data.Length);
                }
                else if (prefix == 128 + 32)
                {
                    ReadByte();
                    ReadOnlySpan<byte> keccakSpan = Read(32);
                    if (keccakSpan.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
                    {
                        keccak = new Hash256StructRef(Keccak.OfAnEmptyString.Bytes);
                    }
                    else if (keccakSpan.SequenceEqual(Keccak.EmptyTreeHash.Bytes))
                    {
                        keccak = new Hash256StructRef(Keccak.EmptyTreeHash.Bytes);
                    }
                    else
                    {
                        keccak = new Hash256StructRef(keccakSpan);
                    }
                }
                else
                {
                    ReadOnlySpan<byte> theSpan = DecodeByteArraySpan();
                    if (theSpan.Length < 32)
                    {
                        buffer[..(32 - theSpan.Length)].Clear();
                    }
                    theSpan.CopyTo(buffer[(32 - theSpan.Length)..]);
                    keccak = new Hash256StructRef(buffer);
                }
            }

            public Address? DecodeAddress()
            {
                int prefix = ReadByte();
                if (prefix == 128)
                {
                    return null;
                }

                if (prefix != 128 + 20)
                {
                    ThrowInvalidPrefix(ref this, prefix);
                }

                byte[] buffer = Read(20).ToArray();
                return new Address(buffer);

                static void ThrowInvalidPrefix(ref ValueDecoderContext ctx, int prefix)
                {
                    throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {ctx.Position} in the message of length {ctx.Data.Length} starting with {ctx.Data[..Math.Min(DebugMessageContentLength, ctx.Data.Length)].ToHexString()}");
                }
            }

            public void DecodeAddressStructRef(out AddressStructRef address)
            {
                int prefix = ReadByte();
                if (prefix == 128)
                {
                    address = new AddressStructRef(Address.Zero.Bytes);
                    return;
                }
                else if (prefix != 128 + 20)
                {
                    ThrowInvalidPrefix(ref this, prefix);
                }

                address = new AddressStructRef(Read(20));
            }

            [DoesNotReturn]
            [StackTraceHidden]
            private static void ThrowInvalidPrefix(ref ValueDecoderContext ctx, int prefix)
            {
                throw new RlpException($"Unexpected prefix of {prefix} when decoding {nameof(Hash256)} at position {ctx.Position} in the message of length {ctx.Data.Length} starting with {ctx.Data[..Math.Min(DebugMessageContentLength, ctx.Data.Length)].ToHexString()}");
            }

            public UInt256 DecodeUInt256(int length = -1)
            {
                ReadOnlySpan<byte> byteSpan = DecodeByteArraySpan();
                if (byteSpan.Length > 32)
                {
                    ThrowDataTooLong();
                }

                if (length == -1)
                {
                    if (byteSpan.Length > 1 && byteSpan[0] == 0)
                    {
                        ThrowNonCanonicalUInt256(Position);
                    }
                }
                else if (byteSpan.Length != length)
                {
                    ThrowInvalidLength(Position);
                }


                return new UInt256(byteSpan, true);

                [DoesNotReturn]
                [StackTraceHidden]
                static void ThrowDataTooLong() => throw new RlpException("UInt256 cannot be longer than 32 bytes");

                [DoesNotReturn]
                [StackTraceHidden]
                static void ThrowNonCanonicalUInt256(int position) => throw new RlpException($"Non-canonical UInt256 (leading zero bytes) at position {position}");

                [DoesNotReturn]
                [StackTraceHidden]
                static void ThrowInvalidLength(int position) => throw new RlpException($"Invalid length at position {position}");
            }

            public BigInteger DecodeUBigInt()
            {
                ReadOnlySpan<byte> bytes = DecodeByteArraySpan();
                if (bytes.Length > 1 && bytes[0] == 0)
                {
                    throw new RlpException($"Non-canonical UBigInt (leading zero bytes) at position {Position}");
                }
                return bytes.ToUnsignedBigInteger();
            }

            public Bloom? DecodeBloom()
            {
                ReadOnlySpan<byte> bloomBytes;

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

                return bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? Bloom.Empty : new Bloom(bloomBytes.ToArray());
            }

            public void DecodeBloomStructRef(out BloomStructRef bloom)
            {
                ReadOnlySpan<byte> bloomBytes;

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
                        bloom = new BloomStructRef(Bloom.Empty.Bytes);
                        return;
                    }
                }

                if (bloomBytes.Length != 256)
                {
                    throw new InvalidOperationException("Incorrect bloom RLP");
                }

                bloom = bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? new BloomStructRef(Bloom.Empty.Bytes) : new BloomStructRef(bloomBytes);
            }

            public ReadOnlySpan<byte> PeekNextItem()
            {
                int length = PeekNextRlpLength();
                ReadOnlySpan<byte> item = Read(length);
                Position -= item.Length;
                return item;
            }

            public readonly bool IsNextItemNull()
            {
                return Data[Position] == 192;
            }

            public int DecodeInt()
            {
                int prefix = ReadByte();

                switch (prefix)
                {
                    case 0:
                        throw new RlpException($"Non-canonical integer (leading zero bytes) at position {Position}");
                    case < 128:
                        return prefix;
                    case 128:
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
                    result <<= 8;
                    if (i <= length)
                    {
                        result |= Data[Position + length - i];
                        if (result == 0)
                        {
                            throw new RlpException($"Non-canonical integer (leading zero bytes) at position {Position}");
                        }
                    }
                }

                Position += length;

                return result;
            }

            public byte[] DecodeByteArray()
            {
                return ByteSpanToArray(DecodeByteArraySpan());
            }

            public ReadOnlySpan<byte> DecodeByteArraySpan()
            {
                int prefix = ReadByte();
                ReadOnlySpan<byte> span = RlpStream.SingleBytes;
                if ((uint)prefix < (uint)span.Length)
                {
                    return span.Slice(prefix, 1);
                }

                if (prefix == 128)
                {
                    return default;
                }

                if (prefix <= 183)
                {
                    int length = prefix - 128;
                    ReadOnlySpan<byte> buffer = Read(length);
                    if (length == 1 && buffer[0] < 128)
                    {
                        ThrowUnexpectedValue(buffer[0]);
                    }

                    return buffer;
                }

                return DecodeLargerByteArraySpan(prefix);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private ReadOnlySpan<byte> DecodeLargerByteArraySpan(int prefix)
            {
                if (prefix < 192)
                {
                    int lengthOfLength = prefix - 183;
                    if (lengthOfLength > 4)
                    {
                        // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                        ThrowUnexpectedLengthOfLength();
                    }

                    int length = DeserializeLength(lengthOfLength);
                    if (length < 56)
                    {
                        ThrowUnexpectedLength(length);
                    }

                    return Read(length);
                }

                ThrowUnexpectedPrefix(prefix);
                return default;
            }

            public Memory<byte>? DecodeByteArrayMemory()
            {
                if (!_sliceMemory)
                {
                    return DecodeByteArraySpan().ToArray();
                }

                if (Memory is null)
                {
                    ThrowNotMemoryBacked();
                }

                int prefix = ReadByte();

                switch (prefix)
                {
                    case < 128:
                        return Memory.Value.Slice(Position - 1, 1);
                    case 128:
                        return Array.Empty<byte>();
                    case <= 183:
                        {
                            int length = prefix - 128;
                            Memory<byte> buffer = ReadSlicedMemory(length);
                            Span<byte> asSpan = buffer.Span;
                            if (length == 1 && asSpan[0] < 128)
                            {
                                ThrowUnexpectedValue(asSpan[0]);
                            }

                            return buffer;
                        }
                    case < 192:
                        {
                            int lengthOfLength = prefix - 183;
                            if (lengthOfLength > 4)
                            {
                                // strange but needed to pass tests - seems that spec gives int64 length and tests int32 length
                                ThrowUnexpectedLengthOfLength();
                            }

                            int length = DeserializeLength(lengthOfLength);
                            if (length < 56)
                            {
                                ThrowUnexpectedLength(length);
                            }

                            return ReadSlicedMemory(length);
                        }
                }

                ThrowUnexpectedPrefix(prefix);
                return default;

                [DoesNotReturn]
                [StackTraceHidden]
                static void ThrowNotMemoryBacked()
                {
                    throw new RlpException("Rlp not backed by a Memory<byte>");
                }
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowUnexpectedPrefix(int prefix)
            {
                throw new RlpException($"Unexpected prefix value of {prefix} when decoding a byte array.");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowUnexpectedLength(int length)
            {
                throw new RlpException($"Expected length greater or equal 56 and was {length}");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowUnexpectedValue(int buffer0)
            {
                throw new RlpException($"Unexpected byte value {buffer0}");
            }

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowUnexpectedLengthOfLength()
            {
                throw new RlpException("Expected length of length less or equal 4");
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
                    if (length == 1 && PeekByte() < 128)
                    {
                        throw new RlpException($"Unexpected byte value {PeekByte()}");
                    }

                    bool result = PeekByte() == 1;
                    SkipBytes(length);
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

                    bool result = PeekByte() == 1;
                    SkipBytes(length);
                    return result;
                }

                throw new RlpException($"Unexpected prefix of {prefix} when decoding a byte array at position {Position} in the message of length {Length} starting with {Description}");
            }

            private readonly string Description => Data[..Math.Min(DebugMessageContentLength, Length)].ToHexString();

            public readonly byte PeekByte()
            {
                return Data[Position];
            }

            private readonly byte PeekByte(int offset)
            {
                return Data[Position + offset];
            }

            private void SkipBytes(int length)
            {
                Position += length;
            }

            public string DecodeString()
            {
                ReadOnlySpan<byte> bytes = DecodeByteArraySpan();
                return Encoding.UTF8.GetString(bytes);
            }

            public long DecodeLong()
            {
                int prefix = ReadByte();

                switch (prefix)
                {
                    case 0:
                        throw new RlpException($"Non-canonical long (leading zero bytes) at position {Position}");
                    case < 128:
                        return prefix;
                    case 128:
                        return 0;
                }

                int length = prefix - 128;
                if (length > 8)
                {
                    throw new RlpException($"Unexpected length of long value: {length}");
                }

                long result = 0;
                for (int i = 8; i > 0; i--)
                {
                    result <<= 8;
                    if (i <= length)
                    {
                        result |= PeekByte(length - i);
                        if (result == 0)
                        {
                            throw new RlpException($"Non-canonical long (leading zero bytes) at position {Position}");
                        }
                    }
                }

                SkipBytes(length);

                return result;
            }

            public ulong DecodeULong()
            {
                int prefix = ReadByte();

                switch (prefix)
                {
                    case 0:
                        throw new RlpException($"Non-canonical ulong (leading zero bytes) at position {Position}");
                    case < 128:
                        return (ulong)prefix;
                    case 128:
                        return 0;
                }

                int length = prefix - 128;
                if (length > 8)
                {
                    throw new RlpException($"Unexpected length of long value: {length}");
                }

                ulong result = 0ul;
                for (int i = 8; i > 0; i--)
                {
                    result <<= 8;
                    if (i <= length)
                    {
                        result |= PeekByte(length - i);
                        if (result == 0)
                        {
                            throw new RlpException($"Non-canonical ulong (leading zero bytes) at position {Position}");
                        }
                    }
                }

                SkipBytes(length);

                return result;
            }

            internal byte[][] DecodeByteArrays()
            {
                int length = ReadSequenceLength();
                if (length is 0)
                {
                    return [];
                }

                int itemsCount = PeekNumberOfItemsRemaining(Position + length);
                byte[][] result = new byte[itemsCount][];

                for (int i = 0; i < itemsCount; i++)
                {
                    result[i] = DecodeByteArray();
                }

                return result;
            }

            public byte DecodeByte()
            {
                byte byteValue = PeekByte();
                if (byteValue < 128)
                {
                    SkipBytes(1);
                    return byteValue;
                }

                if (byteValue == 128)
                {
                    SkipBytes(1);
                    return 0;
                }

                if (byteValue == 129)
                {
                    SkipBytes(1);
                    return ReadByte();
                }

                throw new RlpException($"Unexpected value while decoding byte {byteValue}");
            }

            public T[] DecodeArray<T>(IRlpValueDecoder<T>? decoder = null, bool checkPositions = true,
                T defaultElement = default)
            {
                if (decoder is null)
                {
                    decoder = GetValueDecoder<T>();
                    if (decoder is null)
                    {
                        throw new RlpException($"{nameof(Rlp)} does not support length of {nameof(T)}");
                    }
                }
                int positionCheck = ReadSequenceLength() + Position;
                int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null);
                T[] result = new T[count];
                for (int i = 0; i < result.Length; i++)
                {
                    if (PeekByte() == Rlp.OfEmptySequence[0])
                    {
                        result[i] = defaultElement;
                        Position++;
                    }
                    else
                    {
                        result[i] = decoder.Decode(ref this);
                    }
                }

                return result;
            }

            public readonly bool IsNextItemEmptyArray()
            {
                return PeekByte() == EmptyArrayByte;
            }

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
