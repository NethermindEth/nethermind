// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Abi
{
    public static class AbiEncoderExtensions
    {
        public static byte[] Encode(this IAbiEncoder encoder, AbiEncodingInfo abiEncodingInfo, params object[] arguments)
            => encoder.Encode(abiEncodingInfo.EncodingStyle, abiEncodingInfo.Signature, arguments);

        public static object[] Decode(this IAbiEncoder encoder, AbiEncodingInfo abiEncodingInfo, byte[] data)
            => encoder.Decode(abiEncodingInfo.EncodingStyle, abiEncodingInfo.Signature, data);
    }
}
