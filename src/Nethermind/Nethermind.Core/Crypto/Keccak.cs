// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json.Serialization;
using System.Text.Json;

using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.Crypto
{
    [DebuggerStepThrough]
    [DebuggerDisplay("{ToString()}")]
    public static class ValueKeccak
    {
        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly ValueHash256 OfAnEmptyString = InternalCompute(new byte[] { });

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly ValueHash256 OfAnEmptySequenceRlp = InternalCompute(new byte[] { 192 });

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly ValueHash256 EmptyTreeHash = InternalCompute(new byte[] { 128 });

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static ValueHash256 Zero { get; } = default;

        /// <summary>
        ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
        /// </summary>
        public static ValueHash256 MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

        [DebuggerStepThrough]
        public static ValueHash256 Compute(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return OfAnEmptyString;
            }

            return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
        }

        [DebuggerStepThrough]
        public static ValueHash256 Compute(ReadOnlySpan<byte> input)
        {
            if (input.Length == 0)
            {
                return OfAnEmptyString;
            }

            Unsafe.SkipInit(out ValueHash256 keccak);
            KeccakHash.ComputeHashBytesToSpan(input, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref keccak, 1)));
            return keccak;
        }

        internal static ValueHash256 InternalCompute(byte[] input)
        {
            Unsafe.SkipInit(out ValueHash256 keccak);
            KeccakHash.ComputeHashBytesToSpan(input, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref keccak, 1)));
            return keccak;
        }
    }

    [DebuggerStepThrough]
    [JsonConverter(typeof(KeccakConverter))]
    public static class Keccak
    {
        public const int Size = 32;

        public const int MemorySize =
            MemorySizes.SmallObjectOverhead -
            MemorySizes.RefSize +
            Size;

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly Hash256 OfAnEmptyString = new Hash256(ValueKeccak.InternalCompute(new byte[] { }));

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly Hash256 OfAnEmptySequenceRlp = new Hash256(ValueKeccak.InternalCompute(new byte[] { 192 }));

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static Hash256 EmptyTreeHash = new Hash256(ValueKeccak.InternalCompute(new byte[] { 128 }));

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Hash256 Zero { get; } = new(new byte[Size]);

        /// <summary>
        ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
        /// </summary>
        public static Hash256 MaxValue { get; } = new("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");


        [DebuggerStepThrough]
        public static Hash256 Compute(byte[]? input)
        {
            if (input is null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return new Hash256(KeccakHash.ComputeHashBytes(input));
        }

        [DebuggerStepThrough]
        public static Hash256 Compute(ReadOnlySpan<byte> input)
        {
            return new Hash256(ValueKeccak.Compute(input));
        }

        [DebuggerStepThrough]
        public static Hash256 Compute(string input)
        {
            return new Hash256(ValueKeccak.Compute(input));
        }
    }
}

namespace Nethermind.Serialization.Json
{
    public class KeccakConverter : JsonConverter<Keccak>
    {
        public override Keccak? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            byte[]? bytes = ByteArrayConverter.Convert(ref reader);
            return bytes is null ? null : new Keccak(bytes);
        }

        public override void Write(
            Utf8JsonWriter writer,
            Keccak keccak,
            JsonSerializerOptions options)
        {
            ByteArrayConverter.Convert(writer, keccak.Bytes, skipLeadingZeros: false);
        }
    }
}
