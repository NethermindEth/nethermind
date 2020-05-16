//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core2.Types;
using Nethermind.HashLib;

namespace Nethermind.Core2.Crypto
{
    [DebuggerStepThrough]
    public static class Sha256
    {
        // TODO: verify if this is thread safe
        private static readonly IHash Hash = HashFactory.Crypto.CreateSHA256();

        /// <returns>
        ///     <string>0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470</string>
        /// </returns>
        public static readonly Root RootOfAnEmptyString = new Root(ComputeBytes(new byte[0]));

        public static readonly Bytes32 Bytes32OfAnEmptyString = new Bytes32(ComputeBytes(new byte[0]));

        /// <returns>
        ///     <string>0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347</string>
        /// </returns>
        public static readonly Bytes32 OfAnEmptySequenceRlp = InternalCompute(new byte[] {192});

        /*
        /// <summary>
        ///     0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421
        /// </summary>
        public static Bytes32 EmptyTreeHash = InternalCompute(new byte[] {128});
        */
        
        /*
        /// <returns>
        ///     <string>0x0000000000000000000000000000000000000000000000000000000000000000</string>
        /// </returns>
        public static Bytes32 Zero { get; } = Bytes32.Zero;
        */
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] ComputeBytes(ReadOnlySpan<byte> input)
        {
            return Hash.ComputeBytes(input).GetBytes();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] ComputeBytes(byte[] input)
        {
            return Hash.ComputeBytes(input).GetBytes();
        }

        // [DebuggerStepThrough]
        // public static Bytes32 Compute(byte[] input)
        // {
        //     if (input == null || input.Length == 0)
        //     {
        //         return Bytes32OfAnEmptyString;
        //     }
        //
        //     return Compute(input.AsSpan());
        // }

        [DebuggerStepThrough]
        public static Bytes32 Compute(Span<byte> input)
        {
            if (input == null || input.Length == 0)
            {
                return Bytes32OfAnEmptyString;
            }

            return InternalCompute(input);
        }
        
        // public static void ComputeInPlace(Span<byte> input)
        // {
        //     if (input == null || input.Length == 0)
        //     {
        //         RootOfAnEmptyString.AsSpan().CopyTo(input);
        //     }
        //
        //     byte[] bytes = Hash.ComputeBytes(input.ToArray()).GetBytes();
        //     bytes.AsSpan().CopyTo(input);
        // }

        private static Bytes32 InternalCompute(byte[] input)
        {
            return new Bytes32(Hash.ComputeBytes(input).GetBytes());
        }
        
        private static Bytes32 InternalCompute(ReadOnlySpan<byte> input)
        {
            return new Bytes32(Hash.ComputeBytes(input).GetBytes());
        }

        // [DebuggerStepThrough]
        // public static Bytes32 Compute(string input)
        // {
        //     if (string.IsNullOrWhiteSpace(input))
        //     {
        //         return Bytes32OfAnEmptyString;
        //     }
        //
        //     return InternalCompute(System.Text.Encoding.UTF8.GetBytes(input));
        // }
    }
}