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
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiEncoder
    {
        public byte[] Encode(AbiSignature signature, params object[] arguments)
        {
            List<byte[]> dynamicParts = new List<byte[]>();
            List<byte[]> headerParts = new List<byte[]>();
            BigInteger currentOffset = arguments.Length * AbiType.UInt.LengthInBytes;
            for (int i = 0; i < arguments.Length; i++)
            {
                AbiType type = signature.Types[i];
                if (type.IsDynamic)
                {
                    headerParts.Add(AbiType.UInt.Encode(currentOffset));
                    byte[] encoded = type.Encode(arguments[i]);
                    currentOffset += encoded.Length;
                    dynamicParts.Add(encoded);
                }
                else
                {
                    headerParts.Add(type.Encode(arguments[i]));
                }
            }

            byte[][] encodedParts = new byte[1 + headerParts.Count + dynamicParts.Count][];
            encodedParts[0] = ComputeAddress(signature);
            for (int i = 0; i < headerParts.Count; i++)
            {
                encodedParts[1 + i] = headerParts[i];
            }

            for (int i = 0; i < dynamicParts.Count; i++)
            {
                encodedParts[1 + headerParts.Count + i] = dynamicParts[i];
            }

            return Bytes.Concat(encodedParts);
        }

        private static byte[] ComputeAddress(AbiSignature signature)
        {
            string[] argTypeNames = new string[signature.Types.Length];
            for (int i = 0; i < signature.Types.Length; i++)
            {
                argTypeNames[i] = signature.Types[i].ToString();
            }

            string typeList = string.Join(",", argTypeNames);
            string signatureString = $"{signature.Name}({typeList})";
            Keccak signatureKeccak = Keccak.Compute(signatureString);
            return signatureKeccak.Bytes.Slice(0, 4);
        }

        private static string ComputeSignature(string functionName, AbiType[] abiTypes)
        {
            string[] argTypeNames = new string[abiTypes.Length];
            for (int i = 0; i < abiTypes.Length; i++)
            {
                argTypeNames[i] = abiTypes[i].ToString();
            }

            string typeList = string.Join(",", argTypeNames);
            return $"{functionName}({typeList})";
        }

        public object[] Decode(AbiSignature signature, byte[] data)
        {
            string[] argTypeNames = new string[signature.Types.Length];
            for (int i = 0; i < signature.Types.Length; i++)
            {
                argTypeNames[i] = signature.Types[i].ToString();
            }

            if (!Bytes.AreEqual(data.Slice(0, 4), ComputeAddress(signature)))
            {
                throw new AbiException(
                    $"Signature in encoded ABI data is not consistent with {ComputeSignature(signature.Name, signature.Types)}");
            }

            int position = 4;
            object[] arguments = new object[signature.Types.Length];
            int dynamicPosition = 0;
            for (int i = 0; i < signature.Types.Length; i++)
            {
                AbiType type = signature.Types[i];
                if (type.IsDynamic)
                {
                    // TODO: do not have to decode this - can just jump 32 and check if first call and use dynamic position
                    (BigInteger offset, int nextPosition) = AbiType.UInt.DecodeUInt(data, position);
                    (arguments[i], dynamicPosition) = type.Decode(data, 4 + (int)offset);
                    position = nextPosition;
                }
                else
                {
                    (arguments[i], position) = type.Decode(data, position);
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