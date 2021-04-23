//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Abi
{
    [Flags]
    public enum AbiEncodingStyle
    {
        None = 0,
        IncludeSignature = 1,
        Packed = 2,
        All = 3
    }
    
    public class AbiEncoder : IAbiEncoder
    {
        public static readonly AbiEncoder Instance = new();

        public byte[] Encode(AbiEncodingStyle encodingStyle, AbiSignature signature, params object[] arguments)
        {
            bool packed = (encodingStyle & AbiEncodingStyle.Packed) == AbiEncodingStyle.Packed;

            if (arguments.Length != signature.Types.Length)
            {
                throw new AbiException(
                    $"Insufficient parameters for {signature.Name}. Expected {signature.Types.Length} arguments but got {arguments.Length}");
            }

            List<byte[]> dynamicParts = new();
            List<byte[]> headerParts = new();
            BigInteger currentOffset = arguments.Length * AbiType.UInt256.LengthInBytes;
            for (int i = 0; i < arguments.Length; i++)
            {
                AbiType type = signature.Types[i];
                if (type.IsDynamic)
                {
                    headerParts.Add(AbiType.UInt256.Encode(currentOffset, packed));
                    byte[] encoded = type.Encode(arguments[i], packed);
                    currentOffset += encoded.Length;
                    dynamicParts.Add(encoded);
                }
                else
                {
                    headerParts.Add(type.Encode(arguments[i], packed));
                }
            }

            bool includeSig = encodingStyle == AbiEncodingStyle.IncludeSignature;
            int sigOffset = includeSig ? 1 : 0;
            byte[][] encodedParts = new byte[sigOffset + headerParts.Count + dynamicParts.Count][];

            if (includeSig)
            {
                encodedParts[0] = signature.Address;
            }

            for (int i = 0; i < headerParts.Count; i++)
            {
                encodedParts[sigOffset + i] = headerParts[i];
            }

            for (int i = 0; i < dynamicParts.Count; i++)
            {
                encodedParts[sigOffset + headerParts.Count + i] = dynamicParts[i];
            }

            return Bytes.Concat(encodedParts);
        }

        public object[] Decode(AbiEncodingStyle encodingStyle, AbiSignature signature, byte[] data)
        {
            bool packed = (encodingStyle & AbiEncodingStyle.Packed) == AbiEncodingStyle.Packed; 
            bool includeSig = encodingStyle == AbiEncodingStyle.IncludeSignature;
            int sigOffset = includeSig ? 4 : 0;
            
            string[] argTypeNames = new string[signature.Types.Length];
            for (int i = 0; i < signature.Types.Length; i++)
            {
                argTypeNames[i] = signature.Types[i].ToString();
            }

            int position = 0;
            if (encodingStyle == AbiEncodingStyle.IncludeSignature)
            {
                if (!Bytes.AreEqual(AbiSignature.GetAddress(data), signature.Address))
                {
                    throw new AbiException(
                        $"Signature in encoded ABI data is not consistent with {signature}");
                }

                position = 4;
            }

            object[] arguments = new object[signature.Types.Length];
            int dynamicPosition = 0;
            for (int i = 0; i < signature.Types.Length; i++)
            {
                AbiType type = signature.Types[i];
                if (type.IsDynamic)
                {
                    // TODO: do not have to decode this - can just jump 32 and check if first call and use dynamic position
                    (UInt256 offset, int nextPosition) = AbiType.UInt256.DecodeUInt(data, position, packed);
                    (arguments[i], dynamicPosition) = type.Decode(data, sigOffset + (int)offset, packed);
                    position = nextPosition;
                }
                else
                {
                    (arguments[i], position) = type.Decode(data, position, packed);
                }
            }

            if (Math.Max(position, dynamicPosition) != data.Length)
            {
                throw new AbiException($"Unexpected data at position {position}");
            }

            return arguments;
        }
    }
}
