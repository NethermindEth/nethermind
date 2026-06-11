// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nethermind.Core.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Serialization.Rlp
{
    public delegate T DecodeRlpValue<T>(ref Rlp.ValueDecoderContext ctx);

    /// <summary>
    ///     https://github.com/ethereum/wiki/wiki/RLP
    ///
    ///     Note: Prefer RlpStream to encode instead, which does not create a new byte array on each call.
    /// </summary>
    public partial class Rlp
    {
        public const int LengthOfKeccakRlp = 33;

        public const int LengthOfAddressRlp = 21;

        internal const int DebugMessageContentLength = 2048;

        public const byte EmptyByteArrayByte = 0x80;

        public const byte EmptyListByte = 0xc0; // use bytes to avoid stack overflow

        internal const int LengthOfNull = 1;

        public static readonly Rlp OfEmptyByteArray = new(EmptyByteArrayByte);

        public static readonly Rlp OfZero = OfEmptyByteArray;

        public static readonly Rlp OfEmptyList = new(EmptyListByte);

        internal static readonly Rlp OfEmptyTreeHash = Encode(Keccak.EmptyTreeHash.Bytes); // use bytes to avoid stack overflow

        internal static readonly Rlp OfEmptyStringHash = Encode(Keccak.OfAnEmptyString.Bytes); // use bytes to avoid stack overflow

        internal static readonly Rlp EmptyBloom = Encode(Bloom.Empty.Bytes);
        static Rlp() => RegisterDecoders(Assembly.GetAssembly(typeof(Rlp)));

        /// <summary>
        /// This is not encoding - just a creation of an RLP object, e.g. passing 192 would mean an RLP of an empty sequence.
        /// </summary>
        private Rlp(byte singleByte) => Bytes = [singleByte];

        public Rlp(byte[] bytes) => Bytes = bytes ?? throw new RlpException("RLP cannot be initialized with null bytes");

        public long MemorySize => /* this */ MemorySizes.SmallObjectOverhead +
                                            MemorySizes.Align(MemorySizes.ArrayOverhead + Bytes.Length);

        public byte[] Bytes { get; }

        public byte this[int index] => Bytes[index];

        public int Length => Bytes.Length;

        private static readonly Dictionary<RlpDecoderKey, IRlpDecoder> _decoderBuilder = [];
        private static readonly Lock _decoderLock = new();
        private static readonly CappedArray<byte>[] s_intPreEncodes = CreatePreEncodes();

        public static void ResetDecoders()
        {
            using Lock.Scope _ = _decoderLock.EnterScope();
            _decoderBuilder.Clear();
            _decodersSnapshot = null;
            RegisterDecoders(Assembly.GetAssembly(typeof(Rlp)));
            RegisterDecoder(typeof(Transaction), TxDecoder.Instance);
        }

        public static void RegisterDecoder(RlpDecoderKey key, IRlpDecoder decoder)
        {
            using Lock.Scope _ = _decoderLock.EnterScope();
            _decoderBuilder[key] = decoder;
            _decodersSnapshot = null;
        }

        public static partial void RegisterDecoders(Assembly assembly, bool canOverrideExistingDecoders = false);

        public static T Decode<T>(Rlp oldRlp, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => Decode<T>(oldRlp.Bytes.AsSpan(), rlpBehaviors);

        public static T Decode<T>(byte[]? bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            => Decode<T>((bytes ?? []).AsSpan(), rlpBehaviors);

        public static T Decode<T>(Span<byte> bytes, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ValueDecoderContext valueContext = bytes.AsRlpValueContext();
            return Decode<T>(ref valueContext, rlpBehaviors);
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
                byte[][] arrays = RlpStream.SingleByteArrays;
                if ((uint)value < (uint)arrays.Length)
                {
                    return arrays[value];
                }
            }

            return span.ToArray();
        }

        public static IRlpDecoder<T>? GetDecoder<T>(string key = RlpDecoderKey.Default) => Decoders.TryGetValue(new(typeof(T), key), out IRlpDecoder value) ? value as IRlpDecoder<T> : null;

        public static ArrayPoolList<T> DecodeArrayPool<T>(ref ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
        {
            IRlpDecoder<T>? rlpDecoder = GetDecoder<T>();
            return rlpDecoder is not null
                ? DecodeArrayPool(ref decoderContext, rlpDecoder, rlpBehaviors, limit)
                : throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
        }

        public static ArrayPoolList<T> DecodeArrayPool<T>(ref ValueDecoderContext decoderContext, IRlpDecoder<T> rlpDecoder, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
        {
            ArrayPoolList<T>? result = null;
            try
            {
                int checkPosition = decoderContext.ReadSequenceLength() + decoderContext.Position;
                int length = decoderContext.PeekNumberOfItemsRemaining(checkPosition);
                decoderContext.GuardLimit(length, limit);
                result = new(length);
                for (int i = 0; i < length; i++)
                {
                    result.Add(rlpDecoder.Decode(ref decoderContext, rlpBehaviors));
                }

                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
                {
                    decoderContext.Check(checkPosition);
                }

                return result;
            }
            catch (RlpException)
            {
                DisposeDecodedItemsAndList(result, result?.Count ?? 0);
                throw;
            }
            catch (Exception e)
            {
                DisposeDecodedItemsAndList(result, result?.Count ?? 0);
                throw new RlpException($"Error decoding array of {typeof(T).Name}.", e);
            }
        }

        private static void DisposeDecodedItemsAndList<T>(ArrayPoolList<T>? list, int count)
        {
            if (list is null)
            {
                return;
            }

            try
            {
                DisposeDecodedItems(list, count);
            }
            finally
            {
                list.Dispose();
            }
        }

        private static void DisposeDecodedItems<T>(ArrayPoolList<T> list, int count)
        {
            if (typeof(IDisposable).IsAssignableFrom(typeof(T)))
            {
                for (int i = 0; i < count; i++)
                {
                    ((IDisposable?)list[i])?.Dispose();
                }

                return;
            }

            if (typeof(T).IsValueType || typeof(T).IsSealed)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                if (list[i] is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        public static T Decode<T>(ref ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            IRlpDecoder<T> rlpDecoder = GetDecoder<T>() ??
                throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");

            bool shouldCheckStream = decoderContext.Position == 0 && (rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes;
            T? result;
            try
            {
                result = shouldCheckStream
                    ? rlpDecoder.DecodeComplete(ref decoderContext, rlpBehaviors)
                    : rlpDecoder.Decode(ref decoderContext, rlpBehaviors);
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                throw new RlpException($"Truncated or out-of-bounds RLP while decoding {typeof(T).Name}.", e);
            }
            catch (RlpException)
            {
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new RlpException($"Error decoding {typeof(T).Name}.", e);
            }

            return result;
        }

        public static Rlp Encode<T>(T item, RlpBehaviors behaviors = RlpBehaviors.None)
            => item is Rlp rlp
                ? rlp
                : GetDecoder<T>() is { } rlpDecoder
                    ? rlpDecoder.Encode(item, behaviors)
                    : throw new RlpException($"{nameof(Rlp)} does not support encoding {typeof(T).Name}");

        public static Rlp Encode<T>(T[] items, RlpBehaviors behaviors = RlpBehaviors.None)
            => items is []
                ? OfEmptyList
                : GetDecoder<T>() is { } rlpDecoder
                    ? rlpDecoder.Encode(items, behaviors)
                    : throw new RlpException($"{nameof(Rlp)} does not support encoding {typeof(T).Name}");

        public static Rlp Encode(int[] integers)
        {
            Rlp[] rlpSequence = new Rlp[integers.Length];
            for (int i = 0; i < integers.Length; i++)
            {
                rlpSequence[i] = Encode(integers[i]);
            }

            return Encode(rlpSequence);
        }

        public static CappedArray<byte> EncodeToCappedArray(int item, ICappedArrayPool? bufferPool = null)
        {
            CappedArray<byte>[] cache = s_intPreEncodes;
            if ((uint)item < (uint)cache.Length)
            {
                return cache[item];
            }

            CappedArray<byte> buffer = bufferPool.SafeRent(LengthOf(item));
            buffer.AsRlpStream().Encode(item);
            return buffer;
        }

        public static Rlp Encode(Transaction transaction) => Encode(transaction, false);

        public static Rlp Encode(Transaction transaction, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0) =>
            TxDecoder.Instance.EncodeTx(transaction, RlpBehaviors.SkipTypedWrapping, forSigning, isEip155Enabled, chainId);

        public static Rlp Encode(int value)
        {
            if (value == 0)
            {
                return OfZero;
            }

            return value < 0 ? Encode(new BigInteger(value), 4) : Encode((long)value);
        }

        public static Rlp Encode(long value) => value switch
        {
            < 0 => Encode(new BigInteger(value), 8),
            0L => OfZero,
            < 0x80 => new((byte)value),
            < 0x100 => new([129, (byte)value]),
            < 0x1_0000 => new([130, (byte)(value >> 8), (byte)value]),
            < 0x100_0000 => new([131, (byte)(value >> 16), (byte)(value >> 8), (byte)value]),
            < 0x1_0000_0000 => new([132, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]),
            < 0x100_0000_0000 => new([133, (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]),
            < 0x1_0000_0000_0000 => new([134, (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]),
            < 0x100_0000_0000_0000 => new([135, (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]),
            _ => new([136, (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]),
        };

        // caller is responsible for allocating buffer large enough (max 9 bytes)
        [SuppressMessage("ReSharper", "IntVariableOverflowInUncheckedContext")]
        public static Span<byte> Encode(ulong value, Span<byte> buffer)
        {
            int minLength = LengthOf(value);
            if (buffer.Length < minLength)
            {
                ThrowBufferTooSmall(buffer, minLength);
            }

            switch (value)
            {
                case 0:
                    buffer[0] = 0x80;
                    return buffer[..1];
                case < 0x80:
                    buffer[0] = (byte)value;
                    return buffer[..1];
                case < 0x100:
                    buffer[0] = 129; buffer[1] = (byte)value;
                    return buffer[..2];
                case < 0x1_0000:
                    buffer[0] = 130; buffer[1] = (byte)(value >> 8); buffer[2] = (byte)value;
                    return buffer[..3];
                case < 0x100_0000:
                    buffer[0] = 131; buffer[1] = (byte)(value >> 16); buffer[2] = (byte)(value >> 8); buffer[3] = (byte)value;
                    return buffer[..4];
                case < 0x1_0000_0000:
                    buffer[0] = 132; buffer[1] = (byte)(value >> 24); buffer[2] = (byte)(value >> 16); buffer[3] = (byte)(value >> 8); buffer[4] = (byte)value;
                    return buffer[..5];
                case < 0x100_0000_0000:
                    buffer[0] = 133; buffer[1] = (byte)(value >> 32); buffer[2] = (byte)(value >> 24); buffer[3] = (byte)(value >> 16); buffer[4] = (byte)(value >> 8); buffer[5] = (byte)value;
                    return buffer[..6];
                case < 0x1_0000_0000_0000:
                    buffer[0] = 134; buffer[1] = (byte)(value >> 40); buffer[2] = (byte)(value >> 32); buffer[3] = (byte)(value >> 24); buffer[4] = (byte)(value >> 16); buffer[5] = (byte)(value >> 8); buffer[6] = (byte)value;
                    return buffer[..7];
                case < 0x100_0000_0000_0000:
                    buffer[0] = 135; buffer[1] = (byte)(value >> 48); buffer[2] = (byte)(value >> 40); buffer[3] = (byte)(value >> 32); buffer[4] = (byte)(value >> 24); buffer[5] = (byte)(value >> 16); buffer[6] = (byte)(value >> 8); buffer[7] = (byte)value;
                    return buffer[..8];
                default:
                    buffer[0] = 136; buffer[1] = (byte)(value >> 56); buffer[2] = (byte)(value >> 48); buffer[3] = (byte)(value >> 40); buffer[4] = (byte)(value >> 32); buffer[5] = (byte)(value >> 24); buffer[6] = (byte)(value >> 16); buffer[7] = (byte)(value >> 8); buffer[8] = (byte)value;
                    return buffer[..9];
            }
        }

        // caller is responsible for allocating buffer large enough (max 9 bytes)
        public static Span<byte> Encode(long value, Span<byte> buffer) => Encode(unchecked((ulong)value), buffer);

        public static Rlp Encode(BigInteger bigInteger, int outputLength = -1) => bigInteger == 0 ? OfZero : Encode(bigInteger.ToBigEndianByteArray(outputLength));

        public static Rlp Encode(in UInt256 value, int length = -1)
        {
            if (value.IsZero && length == -1)
            {
                return OfZero;
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

            if (input.Length < RlpHelpers.SmallPrefixBarrier)
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
            int newPosition = position + LengthOfKeccakRlp;
            if ((uint)newPosition > (uint)buffer.Length)
            {
                ThrowArgumentOutOfRangeException();
            }

            Unsafe.Add(ref MemoryMarshal.GetReference(buffer), (nuint)position) = 160;
            Unsafe.As<byte, ValueHash256>(ref Unsafe.Add(ref MemoryMarshal.GetReference(buffer), (nuint)position + 1)) = hash.ValueHash256;
            return newPosition;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowArgumentOutOfRangeException() => throw new ArgumentOutOfRangeException(nameof(buffer));
        }

        [SkipLocalsInit]
        public static Rlp Encode(ReadOnlySpan<byte> input)
        {
            // Special cases return compact/shared instances rather than a freshly allocated array.
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

            // Everything else shares the single serialization code path in Encode(input, output).
            byte[] rlpResult = GC.AllocateUninitializedArray<byte>(LengthOf(input));
            Encode(input, rlpResult);
            return new Rlp(rlpResult);
        }

        public static Rlp Encode(byte[]? input) => input is null or [] ? OfEmptyByteArray : Encode(input.AsSpan());

        /// <summary>
        /// Allocation-free byte-string RLP encoding: writes the encoding of <paramref name="input"/> into
        /// <paramref name="output"/> and returns the number of bytes written.
        /// </summary>
        /// <remarks>
        /// Mirrors <see cref="Encode(ReadOnlySpan{byte})"/> but targets a caller-provided buffer instead of
        /// allocating a new array. The caller must size <paramref name="output"/> to at least
        /// <see cref="LengthOf(ReadOnlySpan{byte})"/> bytes.
        /// </remarks>
        [SkipLocalsInit]
        public static int Encode(ReadOnlySpan<byte> input, Span<byte> output)
        {
            int length = input.Length;
            if (length == 0)
            {
                output[0] = EmptyByteArrayByte;
                return 1;
            }

            if (length == 1 && input[0] < 128)
            {
                output[0] = input[0];
                return 1;
            }

            if (length < RlpHelpers.SmallPrefixBarrier)
            {
                output[0] = (byte)(0x80 + length);
                input.CopyTo(output[1..]);
                return 1 + length;
            }

            int lengthOfLength = LengthOfLength(length);
            output[0] = (byte)(0xb7 + lengthOfLength);
            SerializeLength(length, output.Slice(1, lengthOfLength));
            input.CopyTo(output[(1 + lengthOfLength)..]);
            return 1 + lengthOfLength + length;
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
            if (sequenceLength < RlpHelpers.SmallPrefixBarrier)
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

            if (contentLength < RlpHelpers.SmallPrefixBarrier)
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

            public readonly bool IsSequenceNext() => Data[Position] >= 192;

            public readonly int PeekNumberOfItemsRemaining(int? beforePosition = null, int maxSearch = int.MaxValue)
                => RlpHelpers.CountItems(Data, Position, beforePosition ?? Data.Length, maxSearch);

            public void SkipLength() => Position += PeekPrefixLength();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly int PeekPrefixLength() => RlpHelpers.GetPrefixLength(Data[Position]);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int PeekNextRlpLength() => RlpHelpers.PeekNextRlpLength(Data, Position);

            public ReadOnlySpan<byte> Peek(int length)
            {
                ReadOnlySpan<byte> item = Read(length);
                Position -= item.Length;
                return item;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public (int PrefixLength, int ContentLength) ReadPrefixAndContentLength()
            {
                (int prefixLength, int contentLength) = RlpHelpers.PeekPrefixAndContentLength(Data, Position);
                Position += Math.Max(prefixLength, 1);
                return (prefixLength, contentLength);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public (int PrefixLength, int ContentLength) PeekPrefixAndContentLength()
                => RlpHelpers.PeekPrefixAndContentLength(Data, Position);

            public int ReadSequenceLength()
            {
                int prefix = ReadByte();
                if (prefix < 192)
                {
                    RlpHelpers.ThrowUnexpectedPrefix(prefix);
                }

                if (prefix <= 247)
                {
                    return prefix - 192;
                }

                int lengthOfContentLength = prefix - 247;
                int contentLength = DeserializeLength(lengthOfContentLength);
                if (contentLength < RlpHelpers.SmallPrefixBarrier)
                {
                    RlpHelpers.ThrowUnexpectedLength(contentLength);
                }

                return contentLength;
            }

            private int DeserializeLength(int lengthOfLength)
            {
                if (lengthOfLength == 0 || (uint)lengthOfLength > 4)
                {
                    RlpHelpers.ThrowInvalidLength(lengthOfLength);
                }

                int result = RlpHelpers.DeserializeLengthRef(ref MemoryMarshal.GetReference(Data.Slice(Position, lengthOfLength)), lengthOfLength);
                Position += lengthOfLength;
                return result;
            }

            public byte ReadByte() => Data[Position++];

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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly void Check(int nextCheck)
            {
                if (Position != nextCheck)
                {
                    ThrowCheckpointFailed(nextCheck, Position);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly void CheckEnd()
            {
                if (Position != Length)
                {
                    ThrowCheckEndFailed(Position);
                }
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowCheckpointFailed(int expected, int position) =>
                throw new RlpException($"Data checkpoint failed. Expected {expected} and is {position}");

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowCheckEndFailed(int position) =>
                throw new RlpException($"Data checkpoint failed. Expected to reach the end of the sequence, but is at {position}");

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
                    ThrowKeccakDecodeException(prefix);
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
                    ThrowKeccakDecodeException(prefix);
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

                ReadOnlySpan<byte> theSpan = DecodeByteArraySpan(RlpLimit.L32);
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
                    ThrowKeccakDecodeException(prefix);
                    keccak = default;
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
                    ThrowKeccakDecodeException(prefix);
                    keccak = default;
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
                    ReadOnlySpan<byte> theSpan = DecodeByteArraySpan(RlpLimit.L32);
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
                    RlpHelpers.ThrowUnexpectedPrefix(prefix);
                }

                byte[] buffer = Read(20).ToArray();
                return new Address(buffer);
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
                    RlpHelpers.ThrowUnexpectedPrefix(prefix);
                }

                address = new AddressStructRef(Read(20));
            }

            public UInt256 DecodeUInt256(int length = -1)
            {
                int position = Position;
                if (PeekByte() == 0)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
                }

                ReadOnlySpan<byte> byteSpan = DecodeByteArraySpan(RlpLimit.L32);
                if (byteSpan.Length > 32)
                {
                    RlpHelpers.ThrowUnexpectedIntegerLength(position, byteSpan.Length);
                }

                if (length == -1)
                {
                    if (byteSpan.Length > 1 && byteSpan[0] == 0)
                    {
                        RlpHelpers.ThrowNonCanonicalInteger(position);
                    }
                }
                else if (byteSpan.Length != length)
                {
                    RlpHelpers.ThrowInvalidLength(byteSpan.Length, length);
                }

                return new UInt256(byteSpan, true);
            }

            public EvmWord DecodeEvmWord()
            {
                int position = Position;
                if (PeekByte() == 0)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
                }

                ReadOnlySpan<byte> byteSpan = DecodeByteArraySpan(RlpLimit.L32);
                if (byteSpan.Length > 32)
                {
                    RlpHelpers.ThrowUnexpectedIntegerLength(position, byteSpan.Length);
                }
                if (byteSpan.Length > 1 && byteSpan[0] == 0)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
                }

                EvmWord result = default;
                Span<byte> dest = MemoryMarshal.CreateSpan(ref Unsafe.As<EvmWord, byte>(ref result), 32);
                byteSpan.CopyTo(dest.Slice(32 - byteSpan.Length));
                return result;
            }

            public BigInteger DecodeUBigInt()
            {
                int position = Position;
                ReadOnlySpan<byte> bytes = DecodeByteArraySpan(RlpLimit.L32);
                if (bytes.Length >= 1 && bytes[0] == 0)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
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
                    bloomBytes = Read(Bloom.ByteLength);
                }
                else
                {
                    bloomBytes = DecodeByteArraySpan(RlpLimit.Bloom);
                    if (bloomBytes.Length == 0)
                    {
                        return null;
                    }
                }

                if (bloomBytes.Length != Bloom.ByteLength)
                {
                    throw new InvalidOperationException("Incorrect bloom RLP");
                }

                return bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? Bloom.Empty : new Bloom(bloomBytes);
            }

            public void DecodeBloomStructRef(out BloomStructRef bloom)
            {
                ReadOnlySpan<byte> bloomBytes;

                // tks: not sure why but some nodes send us Blooms in a sequence form
                // https://github.com/NethermindEth/nethermind/issues/113
                if (Data[Position] == 249)
                {
                    Position += 5; // tks: skip 249 1 2 129 127 and read 256 bytes
                    bloomBytes = Read(Bloom.ByteLength);
                }
                else
                {
                    bloomBytes = DecodeByteArraySpan(RlpLimit.Bloom);
                    if (bloomBytes.Length == 0)
                    {
                        bloom = new BloomStructRef(Bloom.Empty.Bytes);
                        return;
                    }
                }

                if (bloomBytes.Length != Bloom.ByteLength)
                {
                    throw new InvalidOperationException("Incorrect bloom RLP");
                }

                bloom = bloomBytes.SequenceEqual(Bloom.Empty.Bytes) ? new BloomStructRef(Bloom.Empty.Bytes) : new BloomStructRef(bloomBytes);
            }

            public ReadOnlySpan<byte> PeekNextItem()
            {
                int length = PeekNextRlpLength();
                return Peek(length);
            }

            public uint DecodeUInt()
            {
                int position = Position;
                int prefix = ReadByte();

                switch (prefix)
                {
                    case 0:
                        return RlpHelpers.ThrowNonCanonicalInteger(position);
                    case < 128:
                        return (uint)prefix;
                    case 128:
                        return 0u;
                }

                int length = prefix - 128;
                if (length > 4)
                {
                    RlpHelpers.ThrowUnexpectedIntegerLength(position, length);
                }

                uint result = 0;
                for (int i = 4; i > 0; i--)
                {
                    result <<= 8;
                    if (i <= length)
                    {
                        result |= Data[Position + length - i];
                        if (result == 0)
                        {
                            RlpHelpers.ThrowNonCanonicalInteger(position);
                        }
                    }
                }

                if (result < 128)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
                }

                Position += length;

                return result;
            }

            public byte[] DecodeByteArray(RlpLimit? limit = null, int size = -1) => ByteSpanToArray(DecodeByteArraySpan(limit, size));

            public ReadOnlySpan<byte> DecodeByteArraySpan(RlpLimit? limit = null, int size = -1)
            {
                int position = Position;
                int prefix = ReadByte();
                ReadOnlySpan<byte> span = RlpStream.SingleBytes;
                if ((uint)prefix < (uint)span.Length)
                {
                    GuardSize(actual: 1, expected: size);
                    return span.Slice(prefix, 1);
                }

                if (prefix is EmptyByteArrayByte)
                {
                    GuardSize(actual: 0, expected: size);
                    return default;
                }

                if (prefix <= 183)
                {
                    int length = prefix - 128;
                    GuardLimit(length, limit);
                    GuardSize(actual: length, expected: size);

                    ReadOnlySpan<byte> buffer = Read(length);

                    if (length == 1 && buffer[0] < 128)
                    {
                        RlpHelpers.ThrowNonCanonicalInteger(position);
                    }

                    return buffer;
                }

                return DecodeLargerByteArraySpan(prefix, limit, size);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private ReadOnlySpan<byte> DecodeLargerByteArraySpan(int prefix, RlpLimit? limit = null, int size = -1)
            {
                if (prefix < 192)
                {
                    int lengthOfLength = prefix - 183;
                    if (lengthOfLength > 4)
                    {
                        RlpHelpers.ThrowSequenceLengthTooLong();
                    }

                    int length = DeserializeLength(lengthOfLength);
                    if (length < RlpHelpers.SmallPrefixBarrier)
                    {
                        RlpHelpers.ThrowUnexpectedLength(length);
                    }

                    GuardSize(actual: length, expected: size);
                    GuardLimit(length, limit);
                    return Read(length);
                }

                RlpHelpers.ThrowUnexpectedPrefix(prefix);
                return default;
            }

            public Memory<byte> DecodeByteArrayMemory(RlpLimit? limit = null)
            {
                if (!_sliceMemory)
                {
                    return DecodeByteArraySpan(limit).ToArray();
                }

                if (Memory is null)
                {
                    ThrowNotMemoryBacked();
                }

                int position = Position;
                int prefix = ReadByte();

                switch (prefix)
                {
                    case < EmptyByteArrayByte:
                        return Memory.Value.Slice(position, 1);
                    case EmptyByteArrayByte:
                        return Array.Empty<byte>();
                    case <= 183:
                        {
                            int length = prefix - 128;
                            Memory<byte> buffer = ReadSlicedMemory(length);
                            Span<byte> asSpan = buffer.Span;

                            if (length == 1 && asSpan[0] < 128)
                            {
                                RlpHelpers.ThrowNonCanonicalInteger(position);
                            }

                            return buffer;
                        }
                    case < 192:
                        {
                            int lengthOfLength = prefix - 183;
                            if (lengthOfLength > 4)
                            {
                                RlpHelpers.ThrowSequenceLengthTooLong();
                            }

                            int length = DeserializeLength(lengthOfLength);
                            if (length < RlpHelpers.SmallPrefixBarrier)
                            {
                                RlpHelpers.ThrowUnexpectedLength(length);
                            }
                            GuardLimit(length, limit);

                            return ReadSlicedMemory(length);
                        }
                }

                RlpHelpers.ThrowUnexpectedPrefix(prefix);
                return default;

                [DoesNotReturn, StackTraceHidden]
                static void ThrowNotMemoryBacked() => throw new RlpException("Rlp not backed by a Memory<byte>");

            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SkipItem() => Position += PeekNextRlpLength();

            public void Reset() => Position = 0;

            public bool DecodeBool()
            {
                byte prefix = ReadByte();
                switch (prefix)
                {
                    case 1:
                        return true;
                    case 128:
                        return false;
                    default:
                        RlpHelpers.ThrowUnexpectedBoolValue(prefix);
                        return false;
                }
            }

            public readonly byte PeekByte() => Data[Position];

            private readonly byte PeekByte(int offset) => Data[Position + offset];

            private void SkipBytes(int length) => Position += length;

            public string DecodeString(RlpLimit? limit = null)
            {
                ReadOnlySpan<byte> bytes = DecodeByteArraySpan(limit);
                return Encoding.UTF8.GetString(bytes);
            }

            public long DecodeLong() => (long)DecodeULong();

            public int DecodeInt() => (int)DecodeUInt();

            /// <summary>
            /// Decodes a non-negative int value. Throws if the decoded value is negative.
            /// Use this for fields that should never be negative.
            /// </summary>
            public int DecodePositiveInt()
            {
                int position = Position;
                int value = DecodeInt();
                if (value < 0)
                    RlpHelpers.ThrowNegativeInteger(position, value);
                return value;
            }

            /// <summary>
            /// Decodes a non-negative long value. Throws if the decoded value is negative.
            /// Use this for fields that should never be negative (e.g., gas values).
            /// </summary>
            public long DecodePositiveLong()
            {
                int position = Position;
                long value = DecodeLong();
                if (value < 0)
                    RlpHelpers.ThrowNegativeInteger(position, value);
                return value;
            }

            public ulong DecodeULong()
            {
                int position = Position;
                int prefix = ReadByte();

                switch (prefix)
                {
                    case 0:
                        return RlpHelpers.ThrowNonCanonicalInteger(position);
                    case < 128:
                        return (ulong)prefix;
                    case 128:
                        return 0;
                }

                int length = prefix - 128;
                if (length > 8)
                {
                    RlpHelpers.ThrowUnexpectedIntegerLength(position, length);
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
                            RlpHelpers.ThrowNonCanonicalInteger(position);
                        }
                    }
                }

                if (result < 128)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
                }

                SkipBytes(length);

                return result;
            }

            public byte[][] DecodeByteArrays(RlpLimit? limit = null, int innerSize = -1)
            {
                int length = ReadSequenceLength();
                if (length is 0)
                {
                    return [];
                }

                int checkPosition = Position + length;
                int itemsCountMax = (limit ?? RlpLimit.DefaultLimit).Limit + 1;
                int itemsCount = PeekNumberOfItemsRemaining(checkPosition, itemsCountMax);
                GuardLimit(itemsCount, limit);
                byte[][] result = new byte[itemsCount][];

                for (int i = 0; i < itemsCount; i++)
                {
                    result[i] = DecodeByteArray(size: innerSize);
                }

                Check(checkPosition);

                return result;
            }

            public ushort DecodeUShort()
            {
                int position = Position;
                int prefix = ReadByte();

                switch (prefix)
                {
                    case 0:
                        RlpHelpers.ThrowNonCanonicalInteger(position);
                        return 0;
                    case < 128:
                        return (ushort)prefix;
                    case 128:
                        return 0;
                }

                int length = prefix - 128;
                if (length > 2)
                {
                    RlpHelpers.ThrowUnexpectedIntegerLength(position, length);
                }

                ushort result = 0;
                for (int i = 2; i > 0; i--)
                {
                    result <<= 8;
                    if (i <= length)
                    {
                        result |= PeekByte(length - i);
                        if (result == 0)
                        {
                            RlpHelpers.ThrowNonCanonicalInteger(position);
                        }
                    }
                }

                if (result < 128)
                {
                    RlpHelpers.ThrowNonCanonicalInteger(position);
                }

                SkipBytes(length);

                return result;
            }

            public byte DecodeByte()
            {
                int position = Position;
                byte byteValue = PeekByte();
                switch (byteValue)
                {
                    case 0:
                        RlpHelpers.ThrowNonCanonicalInteger(position);
                        return 0;
                    case < 128:
                        SkipBytes(1);
                        return byteValue;
                    case 128:
                        SkipBytes(1);
                        return 0;
                    case 129 when PeekByte(1) < 128:
                        RlpHelpers.ThrowNonCanonicalInteger(position);
                        return 0;
                    case 129:
                        SkipBytes(1);
                        return ReadByte();
                    default:
                        RlpHelpers.ThrowUnexpectedByteValue(position, byteValue);
                        return 0;
                }
            }

            /// <summary>
            /// Decodes an RLP sequence into a <typeparamref name="T"/>[], substituting <paramref name="defaultElement"/>
            /// for any element encoded as an empty list (<c>0xc0</c>) instead of invoking <paramref name="decoder"/>.
            /// </summary>
            /// <remarks>
            /// The empty-list-to-default substitution is only safe for reference types, hence the <c>class?</c> constraint.
            /// For a reference type, <c>default(T)</c> is <c>null</c>, which a caller can detect and reject. For a value
            /// type, <c>default(T)</c> is an ordinary zero value indistinguishable from legitimately-decoded data, so a
            /// malformed <c>0xc0</c> element would be silently accepted as zero rather than throwing — a real
            /// consensus-relevant decoding bug (see the EIP-7928 BAL decoder). Value-type arrays must therefore use
            /// <see cref="RlpDecoder{T}.DecodeArray"/>, which decodes every element and rejects <c>0xc0</c>.
            /// </remarks>
            public T[] DecodeArray<T>(IRlpDecoder<T>? decoder = null, bool checkPositions = true, bool allowNulls = false, T defaultElement = default, RlpLimit? limit = null)
                where T : class?
            {
                decoder ??= GetDecoder<T>()
                    ?? throw new RlpException($"{nameof(Rlp)} does not support length of {nameof(T)}");

                int positionCheck = ReadSequenceLength() + Position;
                int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null);
                GuardLimit(count, limit);
                T[] result = new T[count];
                for (int i = 0; i < result.Length; i++)
                {
                    if (PeekByte() == OfEmptyList[0])
                    {
                        if (!allowNulls)
                            RlpHelpers.ThrowNullArrayElement(i);

                        result[i] = defaultElement;
                        Position++;
                    }
                    else
                    {
                        result[i] = decoder.Decode(ref this);

                        if (!allowNulls && result[i] is null)
                            RlpHelpers.ThrowNullArrayElement(i);
                    }
                }

                if (checkPositions)
                {
                    Check(positionCheck);
                }

                return result;
            }

            public T[] DecodeArray<T>(DecodeRlpValue<T> decodeItem, bool checkPositions = true, T defaultElement = default, RlpLimit? limit = null)
            {
                int positionCheck = ReadSequenceLength() + Position;
                int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null);
                GuardLimit(count, limit);
                T[] result = new T[count];
                for (int i = 0; i < result.Length; i++)
                {
                    if (PeekByte() == OfEmptyList[0])
                    {
                        result[i] = defaultElement;
                        Position++;
                    }
                    else
                    {
                        result[i] = decodeItem(ref this);
                    }
                }

                if (checkPositions)
                {
                    Check(positionCheck);
                }

                return result;
            }

            public ArrayPoolList<T> DecodeArrayPoolList<T>(DecodeRlpValue<T> decodeItem, bool checkPositions = true, T defaultElement = default, RlpLimit? limit = null)
            {
                int positionCheck = ReadSequenceLength() + Position;
                int count = PeekNumberOfItemsRemaining(checkPositions ? positionCheck : null);
                GuardLimit(count, limit);
                ArrayPoolList<T> result = new(count, count);
                int i = 0;
                try
                {
                    for (; i < result.Count; i++)
                    {
                        if (PeekByte() == OfEmptyList[0])
                        {
                            result[i] = defaultElement;
                            Position++;
                        }
                        else
                        {
                            result[i] = decodeItem(ref this);
                        }
                    }

                    if (checkPositions)
                    {
                        Check(positionCheck);
                    }

                    return result;
                }
                catch (RlpException)
                {
                    DisposeDecodedItemsAndList(result, i);
                    throw;
                }
                catch (Exception e)
                {
                    DisposeDecodedItemsAndList(result, i);
                    throw new RlpException($"Error decoding array of {typeof(T).Name}.", e);
                }
            }

            public readonly bool IsNextItemEmptyByteArray() => PeekByte() is EmptyByteArrayByte;

            public readonly bool IsNextItemEmptyList() => PeekByte() is EmptyListByte;

            [DoesNotReturn, StackTraceHidden]
            private readonly void ThrowKeccakDecodeException(int prefix)
                => throw new DecodeKeccakRlpException(prefix, Position, Data.Length);

            [StackTraceHidden]
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly void GuardLimit(int count, RlpLimit? limit = null) =>
                Rlp.GuardLimit(count, Length - Position, limit);

            // ReSharper disable once MemberHidesStaticFromOuterClass
            [StackTraceHidden]
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void GuardSize(int actual, int expected) =>
                Rlp.GuardSize(actual, expected);
        }

        public override bool Equals(object? other) => Equals(other as Rlp);

        public override int GetHashCode() => new ReadOnlySpan<byte>(Bytes).FastHash();

        public bool Equals(Rlp? other) => other is not null && Core.Extensions.Bytes.AreEqual(Bytes, other.Bytes);

        public override string ToString() => ToString(true);

        public string ToString(bool withZeroX) => Bytes.ToHexString(withZeroX);

        public static int LengthOf(UInt256? item) => item is null ? LengthOfNull : LengthOf(item.Value);

        public static int LengthOf(in EvmWord value)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<EvmWord, byte>(ref Unsafe.AsRef(in value)), 32);
            int nonZero = bytes.IndexOfAnyExcept((byte)0);
            if (nonZero < 0) return 1;
            int len = 32 - nonZero;
            if (len == 1 && bytes[nonZero] < 128) return 1;
            return 1 + len;
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

        public static int LengthOfByteArrayList(IByteArrayList? list)
        {
            if (list is IRlpWrapper rlpWrapper)
                return rlpWrapper.RlpLength;

            if (list is null || list.Count == 0)
                return LengthOfNull;

            int contentLength = 0;
            for (int i = 0; i < list.Count; i++)
            {
                contentLength += LengthOf(list[i]);
            }

            return LengthOfSequence(contentLength);
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

        public static int LengthOfNonce(ulong _) => 9;

        public static int LengthOf(long value) => LengthOf(unchecked((ulong)value));
        public static int LengthOf(ulong value)
        {
            if (value < 128)
            {
                return 1;
            }
            else
            {
                // everything has a length prefix
                return 1 + sizeof(ulong) - (BitOperations.LeadingZeroCount(value) / 8);
            }
        }

        public static int LengthOf(int value) => LengthOf((long)value);

        public static int LengthOf(uint value) => LengthOf((ulong)value);

        public static int LengthOf(ushort value) => LengthOf((long)value);

        public static int LengthOf(Hash256? item) => item is null ? 1 : 33;

        public static int LengthOf(in ValueHash256? item) => item is null ? 1 : 33;

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

        public static int LengthOf(Address? item) => item is null ? 1 : 21;

        public static int LengthOf(Bloom? bloom) => bloom is null ? 1 : 259;

        public static int LengthOfSequence(int contentLength)
        {
            if (contentLength < RlpHelpers.SmallPrefixBarrier)
            {
                return 1 + contentLength;
            }

            return 1 + LengthOfLength(contentLength) + contentLength;
        }

        public static int LengthOf(byte[]? array) => LengthOf(array.AsSpan());

        public static int LengthOf(Memory<byte>? memory) => LengthOf(memory.GetValueOrDefault().Span);

        public static int LengthOf(in ReadOnlyMemory<byte> memory) => LengthOf(memory.Span);

        public static int LengthOf(IReadOnlyList<byte> array)
        {
            if (array.Count == 0)
            {
                return 1;
            }

            return LengthOfByteString(array.Count, array[0]);
        }

        public static int LengthOf(ReadOnlySpan<byte> array) => array.Length == 0 ? 1 : LengthOfByteString(array.Length, array[0]);

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

            if (length < RlpHelpers.SmallPrefixBarrier)
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

            ReadOnlySpan<char> spanString = value.AsSpan();

            if (spanString.Length == 1 && spanString[0] < 128)
            {
                return 1;
            }

            if (spanString.Length < RlpHelpers.SmallPrefixBarrier)
            {
                return spanString.Length + 1;
            }

            return LengthOfLength(spanString.Length) + 1 + spanString.Length;
        }

        public static int LengthOf(byte value) => 1 + value / 128;

        public static int LengthOf(bool value) => 1;

        public static int LengthOf(LogEntry item) => LogEntryDecoder.Instance.GetLength(item, RlpBehaviors.None);

        public static int LengthOf(BlockInfo item) => BlockInfoDecoder.Instance.GetLength(item, RlpBehaviors.None);

        [AttributeUsage(AttributeTargets.Class)]
        public class SkipGlobalRegistration : Attribute;

        /// <summary>
        /// Optional attribute for RLP decoders.
        /// </summary>
        /// <param name="key">Optional custom key that helps to have more than one decoder for the given type.</param>
        [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
        public sealed class DecoderAttribute(string key = RlpDecoderKey.Default) : Attribute
        {
            public string Key { get; } = key;
        }

        private static ILogger _logger = Static.LogManager.GetClassLogger<Rlp>();

        [StackTraceHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GuardLimit(int count, int bytesLeft, RlpLimit? limit = null)
        {
            RlpLimit l = limit ?? RlpLimit.DefaultLimit;
            // First test rejects either bound being negative.
            if ((bytesLeft | l.Limit) < 0 || (uint)count > (uint)bytesLeft || (uint)count > (uint)l.Limit)
            {
                ThrowCountOverLimit((uint)count, bytesLeft, l);
            }
        }

        [StackTraceHidden]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GuardSize(int actual, int expected)
        {
            // expected == -1 is the sentinel for "no constraint".
            if (expected >= 0 && actual != expected)
            {
                ThrowUnexpectedCount(actual, expected);
            }
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowCountOverLimit(uint count, int bytesLeft, RlpLimit limit)
        {
            string message = string.IsNullOrEmpty(limit.CollectionExpression)
                ? $"Collection count of {count} is over limit {limit.Limit} or {bytesLeft} bytes left"
                : $"Collection count {limit.CollectionExpression} of {count} is over limit {limit.Limit} or {bytesLeft} bytes left";
            _logger.DebugError($"{message}; {new StackTrace()}");
            throw new RlpLimitException(message);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowUnexpectedCount(int count, int expected) =>
            throw new RlpException($"Expected collection count of {expected}, got {count}");

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowBufferTooSmall(Span<byte> buffer, int minLength) =>
            throw new ArgumentException($"Buffer is too small. Minimal length: {minLength}, actual length: {buffer.Length}");

        private static CappedArray<byte>[] CreatePreEncodes()
        {
            const int maxCache = 1024;

            CappedArray<byte>[] cache = new CappedArray<byte>[maxCache];

            for (int i = 0; i < cache.Length; i++)
            {
                int size = LengthOf(i);
                byte[] buffer = new byte[size];
                buffer.AsRlpStream().Encode(i);
                cache[i] = new CappedArray<byte>(buffer);
            }

            return cache;
        }
    }

    public readonly partial struct RlpDecoderKey(Type type, string key = RlpDecoderKey.Default) : IEquatable<RlpDecoderKey>
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

        public bool Equals(RlpDecoderKey other) => _type == other._type && _key.Equals(other._key);

        public override bool Equals(object obj) => obj is RlpDecoderKey key && Equals(key);

        public static bool operator ==(RlpDecoderKey left, RlpDecoderKey right) => left.Equals(right);

        public static bool operator !=(RlpDecoderKey left, RlpDecoderKey right) => !(left == right);

        public override string ToString() => $"({Type.Name},{Key})";
    }
}
