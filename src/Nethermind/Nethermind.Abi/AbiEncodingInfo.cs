// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Abi
{
    public class AbiEncodingInfo(AbiEncodingStyle encodingStyle, AbiSignature signature)
    {
        public AbiEncodingStyle EncodingStyle { get; } = encodingStyle;
        public AbiSignature Signature { get; } = signature;
    }
}
