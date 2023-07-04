// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Abi
{
    public class AbiEncodingInfo
    {
        public AbiEncodingStyle EncodingStyle { get; }
        public AbiSignature Signature { get; }

        public AbiEncodingInfo(AbiEncodingStyle encodingStyle, AbiSignature signature)
        {
            EncodingStyle = encodingStyle;
            Signature = signature;
        }
    }
}
