// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Experimental.Abi.V2;
using NUnit.Framework;

namespace Nethermind.Experimental.Abi.Test;

public class Dynamics
{
    [Test]
    public void PairOfStrings()
    {
        var signature = new AbiSignature("f")
            .With(
                AbiType.String,
                AbiType.String
            );

        var expectedMethodId = Bytes.FromHexString("0x18159cfb");
        signature.MethodId().Should().BeEquivalentTo(expectedMethodId);

        byte[] encoded = V2.Abi.Encode(signature, ("hello", "world"));

        var expected = Bytes.FromHexString(
            "0x18159cfb" + // `MethodId`
            "0000000000000000000000000000000000000000000000000000000000000040" + // Offset to first string
            "0000000000000000000000000000000000000000000000000000000000000080" + // Offset to second string
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of first string
            "68656c6c6f000000000000000000000000000000000000000000000000000000" + // "hello"
            "0000000000000000000000000000000000000000000000000000000000000005" + // Length of second string
            "776f726c64000000000000000000000000000000000000000000000000000000"); // "world"

        encoded.Should().BeEquivalentTo(expected);

        (string a, string b) = V2.Abi.Decode(signature, encoded);
        a.Should().Be("hello");
        b.Should().Be("world");
    }
}
