// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Eez.Execution;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class EezPluginTests
{
    [TestCaseSource(nameof(GenesesWithoutEezl2Code))]
    public void EnsureEezGenesis_WithoutEezl2Code_RefusesToStart(ChainSpec chainSpec) =>
        Assert.That(() => EezPlugin.EnsureEezGenesis(chainSpec), Throws.TypeOf<InvalidConfigurationException>(),
            "enabling EEZ rules on a chain without the EEZL2 predeploy would silently change its consensus");

    [Test]
    public void EnsureEezGenesis_WithEezl2Predeploy_Starts()
    {
        ChainSpec chainSpec = new()
        {
            Allocations = new Dictionary<Address, ChainSpecAllocation>
            {
                [EezConstants.Eezl2Address] = new() { Code = [0x00] },
            },
        };

        Assert.That(() => EezPlugin.EnsureEezGenesis(chainSpec), Throws.Nothing, "an EEZ genesis carries the EEZL2 runtime code");
    }

    private static TestCaseData[] GenesesWithoutEezl2Code() =>
    [
        new TestCaseData(new ChainSpec { Allocations = [] }) { TestName = "NoEezl2Allocation" },
        new TestCaseData(new ChainSpec
        {
            Allocations = new Dictionary<Address, ChainSpecAllocation> { [EezConstants.Eezl2Address] = new() { Code = [] } },
        }) { TestName = "Eezl2AllocationWithoutCode" },
    ];
}
