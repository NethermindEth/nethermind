// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class IdentityPrecompileTests
{
    [Test]
    public void Metadata_matches_the_standard_identity_precompile()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(IdentityPrecompile.Address, Is.EqualTo(Address.FromNumber(4)));
            Assert.That(IdentityPrecompile.Instance.Name, Is.EqualTo("ID"));
            Assert.That(IdentityPrecompile.Instance.SupportsCaching, Is.False);
        }
    }

    [TestCase(0, 0UL)]
    [TestCase(1, 3UL)]
    [TestCase(31, 3UL)]
    [TestCase(32, 3UL)]
    [TestCase(33, 6UL)]
    [TestCase(64, 6UL)]
    public void Gas_cost_matches_identity_word_pricing(int inputLength, ulong expectedDataGas)
    {
        byte[] input = new byte[inputLength];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IdentityPrecompile.Instance.BaseGasCost(Amsterdam.Instance), Is.EqualTo(15UL));
            Assert.That(IdentityPrecompile.Instance.DataGasCost(input, Amsterdam.Instance), Is.EqualTo(expectedDataGas));
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(31)]
    [TestCase(32)]
    [TestCase(33)]
    [TestCase(64)]
    public void Run_succeeds_and_returns_an_owned_byte_for_byte_copy(int inputLength)
    {
        byte[] input = new byte[inputLength];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i * 37 + 11);
        }

        Result<byte[]> result = IdentityPrecompile.Instance.Run(input, Amsterdam.Instance);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Data, Is.Not.Null);

        byte[] output = result.Data!;
        Assert.That(output, Is.EqualTo(input));

        if (input.Length == 0)
        {
            return;
        }

        Assert.That(output, Is.Not.SameAs(input));
        byte originalInputByte = input[0];
        output[0] ^= 0xff;
        Assert.That(input[0], Is.EqualTo(originalInputByte));
    }
}
