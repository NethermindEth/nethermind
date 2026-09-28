// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Eez.Config;
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

    [TestCaseSource(nameof(IncompleteFollowerConfigs))]
    public void EnsureFollowerConfig_FieldMissingOrInvalid_RefusesToStart(EezConfig config, string field) =>
        Assert.That(() => EezPlugin.EnsureFollowerConfig(config), Throws.TypeOf<InvalidConfigurationException>().With.Message.Contains(field),
            "a follower that cannot reach or recognize its rollup on L1 must not start");

    [Test]
    public void EnsureFollowerConfig_Complete_Starts() =>
        Assert.That(() => EezPlugin.EnsureFollowerConfig(FollowerConfig()), Throws.Nothing, "every field the follower needs is set");

    [Test]
    public void EnsureFollowerConfig_FollowerDisabled_IgnoresItsFields() =>
        Assert.That(() => EezPlugin.EnsureFollowerConfig(new EezConfig()), Throws.Nothing, "a node that only executes EEZ blocks needs no L1");

    private static EezConfig FollowerConfig() => new()
    {
        FollowerEnabled = true,
        L1RpcUrl = "http://127.0.0.1:8545",
        L1ChainId = 3151908,
        RegistryAddress = "0x5fbdb2315678afecb367f032d93f642f64180aa3",
        RegistryDeployBlock = 1,
        RollupId = 1,
    };

    private static TestCaseData[] IncompleteFollowerConfigs()
    {
        return
        [
            Case(static c => c.L1RpcUrl = null, nameof(IEezConfig.L1RpcUrl)),
            Case(static c => c.RegistryAddress = "0x5fbdb", nameof(IEezConfig.RegistryAddress)),
            Case(static c => c.RegistryDeployBlock = 0, nameof(IEezConfig.RegistryDeployBlock)),
            Case(static c => c.RollupId = 0, nameof(IEezConfig.RollupId)),
            Case(static c => c.L1ChainId = 0, nameof(IEezConfig.L1ChainId)),
            Case(static c => c.L2BlockTimeSeconds = 0, nameof(IEezConfig.L2BlockTimeSeconds)),
            Case(static c => c.L1LogScanBlocks = 0, nameof(IEezConfig.L1LogScanBlocks)),
            Case(static c => c.L1PollingIntervalMs = 0, nameof(IEezConfig.L1PollingIntervalMs)),
        ];

        static TestCaseData Case(Action<EezConfig> spoil, string field)
        {
            EezConfig config = FollowerConfig();
            spoil(config);
            return new TestCaseData(config, field) { TestName = field };
        }
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
