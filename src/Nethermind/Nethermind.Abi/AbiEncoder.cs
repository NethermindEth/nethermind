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
            bool includeSig = encodingStyle == AbiEncodingStyle.IncludeSignature;

            if (arguments.Length != signature.Types.Length)
            {
                throw new AbiException($"Insufficient parameters for {signature.Name}. Expected {signature.Types.Length} arguments but got {arguments.Length}");
            }
            
            byte[][] encodedArguments = AbiType.EncodeSequence(signature.Types.Length, signature.Types, arguments, packed, includeSig ? 1 : 0);
            
            if (includeSig)
            {
                encodedArguments[0] = signature.Address.ToArray();
            }
            
            return Bytes.Concat(encodedArguments);
        }

        public object[] Decode(AbiEncodingStyle encodingStyle, AbiSignature signature, Memory<byte> data)
        {
            bool packed = (encodingStyle & AbiEncodingStyle.Packed) == AbiEncodingStyle.Packed; 
            bool includeSig = encodingStyle == AbiEncodingStyle.IncludeSignature;
            int sigOffset = includeSig ? 4 : 0;
            if (includeSig)
            {
                if (!Bytes.AreEqual(AbiSignature.GetAddress(data).Span, signature.Address.Span))
                {
                    throw new AbiException($"Signature in encoded ABI data is not consistent with {signature}");
                }
            }
            
            (object[] arguments, int position) = AbiType.DecodeSequence(signature.Types.Length, signature.Types, data, packed, sigOffset);

            if (position != data.Length)
            {
                throw new AbiException($"Unexpected data at position {position}");
            }

            return arguments;
        }
    }
}
