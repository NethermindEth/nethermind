// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                encodedArguments[0] = signature.Address;
            }

            return Bytes.Concat(encodedArguments);
        }

        public object[] Decode(AbiEncodingStyle encodingStyle, AbiSignature signature, byte[] data)
        {
            bool packed = (encodingStyle & AbiEncodingStyle.Packed) == AbiEncodingStyle.Packed;
            bool includeSig = encodingStyle == AbiEncodingStyle.IncludeSignature;
            int sigOffset = includeSig ? 4 : 0;
            if (includeSig)
            {
                if (!Bytes.AreEqual(AbiSignature.GetAddress(data), signature.Address))
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
