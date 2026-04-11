// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Abi.Test
{
    public class AbiEncoderExtensionsTests
    {
        [Test]
        public void Encode_should_be_called()
        {
            IAbiEncoder abi = Substitute.For<IAbiEncoder>();
            object[] parameters = new object[] { "p1" };
            AbiSignature abiSignature = new("test", AbiType.String);
            const AbiEncodingStyle abiEncodingStyle = AbiEncodingStyle.Packed;

            abi.Encode(new AbiEncodingInfo(abiEncodingStyle, abiSignature), parameters);
            abi.Received().Encode(abiEncodingStyle, abiSignature, parameters);
        }

        [Test]
        public void Decode_should_be_called()
        {
            IAbiEncoder abi = Substitute.For<IAbiEncoder>();
            byte[] data = new byte[] { 100, 200 };
            AbiSignature abiSignature = new("test", AbiType.String);
            const AbiEncodingStyle abiEncodingStyle = AbiEncodingStyle.Packed;

            abi.Decode(new AbiEncodingInfo(abiEncodingStyle, abiSignature), data);
            abi.Received().Decode(abiEncodingStyle, abiSignature, data);
        }
    }
}
