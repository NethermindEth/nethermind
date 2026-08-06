// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Abi.Test
{
    // A misforwarded argument makes the substitute return null. The identity asserts
    // then pin both the unpacking and the return propagation.
    public class AbiEncoderExtensionsTests
    {
        [Test]
        public void Encode_forwards_unpacked_info_and_returns_the_encoder_result()
        {
            IAbiEncoder abi = Substitute.For<IAbiEncoder>();
            object[] parameters = new object[] { "p1" };
            AbiSignature abiSignature = new("test", AbiType.String);
            const AbiEncodingStyle abiEncodingStyle = AbiEncodingStyle.Packed;
            byte[] encoded = new byte[] { 1, 2, 3 };
            abi.Encode(abiEncodingStyle, abiSignature, parameters).Returns(encoded);

            Assert.That(abi.Encode(new AbiEncodingInfo(abiEncodingStyle, abiSignature), parameters), Is.SameAs(encoded));
        }

        [Test]
        public void Decode_forwards_unpacked_info_and_returns_the_encoder_result()
        {
            IAbiEncoder abi = Substitute.For<IAbiEncoder>();
            byte[] data = new byte[] { 100, 200 };
            AbiSignature abiSignature = new("test", AbiType.String);
            const AbiEncodingStyle abiEncodingStyle = AbiEncodingStyle.Packed;
            object[] decoded = new object[] { "decoded" };
            abi.Decode(abiEncodingStyle, abiSignature, data).Returns(decoded);

            Assert.That(abi.Decode(new AbiEncodingInfo(abiEncodingStyle, abiSignature), data), Is.SameAs(decoded));
        }
    }
}
