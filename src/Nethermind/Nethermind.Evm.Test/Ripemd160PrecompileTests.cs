// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

// Test data from RFC 2286.
public class Ripemd160PrecompileTests
{
    [TestCase("", "0000000000000000000000009c1185a5c5e9fc54612808977ee8f548b2258d31", true)]
    [TestCase("61", "0000000000000000000000000bdc9d2d256b3ee9daae347be6f4dc835a467ffe", true)]
    [TestCase("616263", "0000000000000000000000008eb208f7e05d987a9b044a8e98c6b087f15a0bfc", true)]
    [TestCase("6d65737361676520646967657374", "0000000000000000000000005d0689ef49d2fae572b881b123a85ffa21595f36", true)]
    public void Test(string input, string output, bool status)
    {
        byte[] inputData = Convert.FromHexString(input);
        (byte[] outputData, bool outcome) = Ripemd160Precompile.Instance.Run(inputData, MuirGlacier.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome, Is.EqualTo(status));
            Assert.That(outputData, Is.EqualTo(Convert.FromHexString(output)));
        }
    }
}
