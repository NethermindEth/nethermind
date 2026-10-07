// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Crypto
{
    [DebuggerStepThrough]
    [DebuggerDisplay("{ToString()}")]
    public static class ValueKeccak
    {
        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        // Literal digests: a static initializer that runs the hash is not evaluable by ILC's static preinitializer,
        // so every use of these fields would otherwise pay a class-constructor check (notably in the zkVM guest).
        public static readonly ValueHash256 OfAnEmptyString = new((ReadOnlySpan<byte>)[0xc5, 0xd2, 0x46, 0x01, 0x86, 0xf7, 0x23, 0x3c, 0x92, 0x7e, 0x7d, 0xb2, 0xdc, 0xc7, 0x03, 0xc0, 0xe5, 0x00, 0xb6, 0x53, 0xca, 0x82, 0x27, 0x3b, 0x7b, 0xfa, 0xd8, 0x04, 0x5d, 0x85, 0xa4, 0x70]);

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly ValueHash256 OfAnEmptySequenceRlp = new((ReadOnlySpan<byte>)[0x1d, 0xcc, 0x4d, 0xe8, 0xde, 0xc7, 0x5d, 0x7a, 0xab, 0x85, 0xb5, 0x67, 0xb6, 0xcc, 0xd4, 0x1a, 0xd3, 0x12, 0x45, 0x1b, 0x94, 0x8a, 0x74, 0x13, 0xf0, 0xa1, 0x42, 0xfd, 0x40, 0xd4, 0x93, 0x47]);

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static readonly ValueHash256 EmptyTreeHash = new((ReadOnlySpan<byte>)[0x56, 0xe8, 0x1f, 0x17, 0x1b, 0xcc, 0x55, 0xa6, 0xff, 0x83, 0x45, 0xe6, 0x92, 0xc0, 0xf8, 0x6e, 0x5b, 0x48, 0xe0, 0x1b, 0x99, 0x6c, 0xad, 0xc0, 0x01, 0x62, 0x2f, 0xb5, 0xe3, 0x63, 0xb4, 0x21]);

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static ValueHash256 Zero { get; } = default;

        /// <summary>
        ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
        /// </summary>
        public static ValueHash256 MaxValue { get; } = new((ReadOnlySpan<byte>)[0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]);

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
        [SkipLocalsInit]
        public static ValueHash256 Compute(ReadOnlySpan<byte> input)
        {
            if (input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return KeccakHash.ComputeHash256(input);
        }

        [SkipLocalsInit]
        internal static ValueHash256 InternalCompute(byte[] input)
        {
            Unsafe.SkipInit(out ValueHash256 keccak);
            KeccakHash.ComputeHashBytesToSpan(input, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref keccak, 1)));
            return keccak;
        }
    }

    [DebuggerStepThrough]
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
        public static readonly Hash256 OfAnEmptyString = new(ValueKeccak.OfAnEmptyString);

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly Hash256 OfAnEmptySequenceRlp = new(ValueKeccak.OfAnEmptySequenceRlp);

        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static Hash256 EmptyTreeHash = new(ValueKeccak.EmptyTreeHash);

        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Hash256 Zero { get; } = new(ValueKeccak.Zero);

        /// <summary>
        ///     <string>0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff</string>
        /// </summary>
        public static Hash256 MaxValue { get; } = new(ValueKeccak.MaxValue);


        [DebuggerStepThrough]
        public static Hash256 Compute(byte[]? input)
        {
            if (input is null || input.Length == 0)
            {
                return OfAnEmptyString;
            }

            return new Hash256(ValueKeccak.Compute(input));
        }

        [DebuggerStepThrough]
        public static Hash256 Compute(ReadOnlySpan<byte> input) => new(ValueKeccak.Compute(input));

        [DebuggerStepThrough]
        public static Hash256 Compute(string input) => new(ValueKeccak.Compute(input));
    }
}
