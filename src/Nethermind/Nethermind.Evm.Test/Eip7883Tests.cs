// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Evm.Precompiles;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip7883Tests
{
    [Test]
    public void ModExp_before_eip_activated()
    {
        var inputBytes = new byte[96];
        Assert.DoesNotThrow(() => ModExpPrecompile.Instance.Run(inputBytes, London.Instance));
        long gas = ModExpPrecompile.Instance.DataGasCost(inputBytes, London.Instance);
        gas.Should().Be(200);
    }

    [Test]
    public void ModExp_after_eip_activated()
    {
        var spec = new ReleaseSpec
        {
            IsEip7883Enabled = true,
            IsEip2565Enabled = true,
        };
        var inputBytes = new byte[96];
        Assert.DoesNotThrow(() => ModExpPrecompile.Instance.Run(inputBytes, spec));
        long gas = ModExpPrecompile.Instance.DataGasCost(inputBytes, spec);
        gas.Should().Be(500);
    }
}
