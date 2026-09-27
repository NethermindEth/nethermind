// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezSpecProviderTests
{
    [TestCaseSource(nameof(DepositContractCases))]
    public void GetSpec_DepositContract_FallsBackToMainnetOnlyWhenUnset(IReleaseSpec inner, Address? expected)
    {
        EezSpecProvider specProvider = new(new TestSpecProvider(inner));

        Assert.That(specProvider.GetSpec(new ForkActivation(1)).DepositContractAddress, Is.EqualTo(expected),
            "a genesis without depositContractAddress uses the mainnet contract; a configured one is kept");
    }

    [Test]
    public void GetSpec_SameInnerSpec_ReturnsTheSameDecoratedInstance()
    {
        EezSpecProvider specProvider = new(new TestSpecProvider(new OverridableReleaseSpec(Osaka.Instance) { DepositContractAddress = Address.Zero }));

        IReleaseSpec first = specProvider.GetSpec(new ForkActivation(1));

        Assert.That(first, Is.InstanceOf<EezReleaseSpec>(), "precondition: the spec needs the deposit contract default");
        Assert.That(specProvider.GetSpec(new ForkActivation(2)), Is.SameAs(first), "the hot path must not allocate a decorator per lookup");
        Assert.That(specProvider.GenesisSpec, Is.SameAs(first), "every lookup of the same fork shares one decorator");
    }

    [Test]
    public void GetSpec_SpecNamingADepositContract_IsServedUndecorated()
    {
        IReleaseSpec inner = new OverridableReleaseSpec(Osaka.Instance) { DepositContractAddress = TestItem.AddressA };
        EezSpecProvider specProvider = new(new TestSpecProvider(inner));

        Assert.That(specProvider.GetSpec(new ForkActivation(1)), Is.SameAs(inner),
            "a spec with nothing to override keeps its direct property reads on the processing path");
    }

    private static TestCaseData[] DepositContractCases() =>
    [
        new TestCaseData(new OverridableReleaseSpec(Osaka.Instance) { DepositContractAddress = Address.Zero }, Eip6110Constants.MainnetDepositContractAddress)
            { TestName = "GethGenesisWithoutDepositContract" },
        new TestCaseData(new OverridableReleaseSpec(Osaka.Instance) { DepositContractAddress = null }, Eip6110Constants.MainnetDepositContractAddress)
            { TestName = "ChainSpecWithoutDepositContract" },
        new TestCaseData(new OverridableReleaseSpec(Osaka.Instance) { DepositContractAddress = TestItem.AddressA }, TestItem.AddressA)
            { TestName = "ConfiguredDepositContract" },
        new TestCaseData(new OverridableReleaseSpec(Cancun.Instance) { DepositContractAddress = null }, null)
            { TestName = "BeforeEip6110" },
    ];
}
