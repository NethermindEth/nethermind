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
    public delegate T DecodeRlpValue<T>(ref RlpReader ctx);

    /// <summary>
    ///     https://github.com/ethereum/wiki/wiki/RLP
    ///
    ///     Note: Prefer RlpWriter to encode into caller-owned buffers.
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
            RlpReader reader = new(bytes);
            return Decode<T>(ref reader, rlpBehaviors);
        }

        public static IRlpDecoder<T>? GetDecoder<T>(string key = RlpDecoderKey.Default) => Decoders.TryGetValue(new(typeof(T), key), out IRlpDecoder value) ? value as IRlpDecoder<T> : null;

        public static ArrayPoolList<T> DecodeArrayPool<T>(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
        {
            IRlpDecoder<T>? rlpDecoder = GetDecoder<T>();
            return rlpDecoder is not null
                ? DecodeArrayPool(ref decoderContext, rlpDecoder, rlpBehaviors, limit)
                : throw new RlpException($"{nameof(Rlp)} does not support decoding {typeof(T).Name}");
        }

        public static ArrayPoolList<T> DecodeArrayPool<T>(ref RlpReader decoderContext, IRlpDecoder<T> rlpDecoder, RlpBehaviors rlpBehaviors = RlpBehaviors.None, RlpLimit? limit = null)
        {
            ArrayPoolList<T>? result = null;
            try
            {
                int checkPosition = decoderContext.ReadSequenceLength() + decoderContext.Position;
                int length = decoderContext.PeekNumberOfItemsRemaining(checkPosition, (limit ?? RlpLimit.DefaultLimit).Limit + 1);
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

        internal static void DisposeDecodedItemsAndList<T>(ArrayPoolList<T>? list, int count)
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

        public static T Decode<T>(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
            RlpWriter writer = new(buffer);
            writer.Encode(item);
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

        public static Rlp Encode(ulong value) => value switch
        {
            0UL => OfZero,
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
            if (_logger.IsTrace)
            {
                string message = string.IsNullOrEmpty(limit.CollectionExpression)
                    ? $"Collection count of {count} is over limit {limit.Limit} or {bytesLeft} bytes left"
                    : $"Collection count {limit.CollectionExpression} of {count} is over limit {limit.Limit} or {bytesLeft} bytes left";
                _logger.Error($"{message}; {new StackTrace()}");

                throw new RlpLimitException(message);
            }

            throw new RlpLimitException("An RLP limit exceeded");
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
                RlpWriter writer = new(buffer);
                writer.Encode(i);
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
