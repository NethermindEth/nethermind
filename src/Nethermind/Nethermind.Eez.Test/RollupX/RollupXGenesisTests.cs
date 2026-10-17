// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Eez.Execution;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class RollupXGenesisTests
{
    private const ulong ReferenceTimestamp = 1_790_868_300;
    private static readonly Hash256 ReferenceHash = new("0x7871b9260dc27b57cc3d7539dfe08295f2738c20491243823315b9a681ba3648");
    private static readonly Hash256 ReferenceRandao = new("0xfb9f935d8de599e5742ccab4dfa29518df338a76e385b1295de2e39bbd585a7b");
    private const string HistoryCode =
        "0x3373fffffffffffffffffffffffffffffffffffffffe14604657602036036042575f35600143038111604257611fff81430311604257611fff9006545f5260205ff35b5f5ffd5b5f35611fff60014303065500";

    [Test]
    public void FromGenesis_ChiadoRollupXSection_ReadsTheSchedule()
    {
        EezChainRules rules = EezChainRules.FromGenesis(Genesis());

        Assert.That(rules.IsRollupX, Is.True, "a rollupX section selects the RollupX rules");
        Assert.That(rules.Eezl2Address, Is.EqualTo(EezConstants.RollupXEezl2Address));
        RollupXSchedule schedule = rules.Schedule!;
        Assert.That((schedule.BlockIntervalSeconds, schedule.SyncIntervalBlocks), Is.EqualTo((1UL, 5U)));
        Assert.That(schedule.L1Reference, Is.EqualTo(new RollupXL1Reference(10200, 23_318_297, ReferenceHash, ReferenceTimestamp, ReferenceRandao)));
        Assert.That(schedule.Timestamp(7), Is.EqualTo(ReferenceTimestamp + 10 + 7), "block n sits n intervals after genesis");
        Assert.That(schedule.IsSyncPosition(10) && !schedule.IsSyncPosition(11), Is.True, "every fifth block is a Sync position");
    }

    /// <summary>
    /// The eez.dev genesis, rebuilt from its live block-zero state and Chiado reference, reads as the Chiado
    /// schedule and builds to the initial block hash its registry recorded.
    /// </summary>
    [Test]
    public void FromGenesisFile_EezDevChiado_BuildsTheRegisteredGenesisHash()
    {
        string path = StatelessFixtures.PathOf("rollupx/eezdev-699069", "genesis.json");
        EezChainRules rules = EezChainRules.FromGenesisFile(path);
        using FileStream file = File.OpenRead(path);
        ChainSpec chainSpec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(file);
        ISpecProvider specProvider = new EezSpecProvider(new ChainSpecBasedSpecProvider(chainSpec), rules);
        IWorldState state = TestWorldStateFactory.CreateForTest();
        GenesisBuilder builder = new(chainSpec, specProvider, state, Substitute.For<ITransactionProcessor>(), Substitute.For<IGenesisPostProcessor>());

        Block genesis;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            genesis = builder.Build();
        }

        Assert.That(rules.Schedule!.L1Reference, Is.EqualTo(new RollupXL1Reference(10200, 23_413_703,
            new Hash256("0x5ac534c090ac3b1383dd8a7647a9df20af2a358dec370e708e1c48bd7d6488ad"), 1_791_397_980,
            new Hash256("0x1af463d6327b8c6b5a5bb581238f0b136d2b93bce04a35b9672734f0383f44ee"))));
        Assert.That(genesis.StateRoot, Is.EqualTo(new Hash256("0x8255029550a16e602838f463ca67bfc560ff9841d576905e2412844de258c18e")));
        Assert.That(genesis.Hash, Is.EqualTo(new Hash256("0x05227a1af51a988231b625b276c6b29594f0a93090efd93463934cd1bf6e606e")));
    }

    [Test]
    public void FromGenesis_NoRollupXSection_IsRollup0()
    {
        EezChainRules rules = EezChainRules.FromGenesis(Genesis(static g => g["config"]!.AsObject().Remove("rollupX")));

        Assert.That(rules, Is.SameAs(EezChainRules.Rollup0));
        Assert.That(rules.BurnedAtBlockStart, Is.Empty, "rollup0 burns nothing");
        Assert.That(rules.BaseFeeMaxChangeDenominator, Is.Null, "rollup0 keeps the fork's EIP-1559 denominator");
    }

    [TestCaseSource(nameof(InvalidGenesis))]
    public void FromGenesis_BrokenRule_Throws(Action<JsonObject> mutate) =>
        Assert.Throws<InvalidDataException>(() => EezChainRules.FromGenesis(Genesis(mutate)));

    private static TestCaseData[] InvalidGenesis() =>
    [
        Case("UnknownSectionKey", static g => Section(g)["extra"] = 1),
        Case("UnknownReferenceKey", static g => Reference(g)["parentHash"] = ReferenceHash.ToString()),
        Case("MissingInterval", static g => Section(g).Remove("blockIntervalSeconds")),
        Case("HexInterval", static g => Section(g)["blockIntervalSeconds"] = "0x1"),
        Case("ZeroInterval", static g => Section(g)["blockIntervalSeconds"] = 0),
        Case("SinglePosition", static g => Section(g)["syncIntervalBlocks"] = 1),
        Case("ZeroReferenceHash", static g => Reference(g)["hash"] = Keccak.Zero.ToString()),
        Case("ChiadoWithMainnetSchedule", static g =>
        {
            Section(g)["blockIntervalSeconds"] = 2;
            Section(g)["syncIntervalBlocks"] = 6;
        }),
        Case("MainnetWithChiadoSchedule", static g => Reference(g)["chainId"] = 1),
        Case("GenesisOffTheSlotGrid", static g => g["timestamp"] = $"0x{ReferenceTimestamp + 7:x}"),
        Case("GenesisBeforeItsReference", static g => g["timestamp"] = $"0x{ReferenceTimestamp - 5:x}"),
        Case("MixHashIsNotTheReferenceRandao", static g => g["mixHash"] = Keccak.Zero.ToString()),
        Case("PragueNotAtGenesis", static g => g["config"]!["pragueTime"] = 5),
        Case("OsakaMissing", static g => g["config"]!.AsObject().Remove("osakaTime")),
        Case("AmsterdamScheduled", static g => g["config"]!["amsterdamTime"] = 100),
        Case("DaoFork", static g => g["config"]!["daoForkBlock"] = 0),
        Case("TerminalDifficultyNotPassed", static g => g["config"]!["terminalTotalDifficultyPassed"] = false),
        Case("TwoGweiBaseFee", static g => g["baseFeePerGas"] = "0x77359400"),
        Case("NoHistoryPredeploy", static g => g["alloc"]!.AsObject().Remove("0x0000F90827F1C53a10cb7A02335B175320002935")),
        Case("HistoryPredeployWithOtherCode", static g => g["alloc"]!["0x0000F90827F1C53a10cb7A02335B175320002935"]!["code"] = "0x00"),
        Case("HistoryPredeployWithStorage", static g => g["alloc"]!["0x0000F90827F1C53a10cb7A02335B175320002935"]!["storage"] =
            new JsonObject { ["0x01"] = "0x01" }),
    ];

    private static TestCaseData Case(string name, Action<JsonObject> mutate) => new TestCaseData(mutate).SetName(name);

    private static JsonObject Section(JsonObject genesis) => genesis["config"]!["rollupX"]!.AsObject();

    private static JsonObject Reference(JsonObject genesis) => Section(genesis)["l1Reference"]!.AsObject();

    private static JsonElement Genesis(Action<JsonObject>? mutate = null)
    {
        JsonObject config = new() { ["chainId"] = 696990 };
        foreach (string fork in new[] { "homesteadBlock", "eip150Block", "eip155Block", "eip158Block", "byzantiumBlock", "constantinopleBlock",
                     "petersburgBlock", "istanbulBlock", "berlinBlock", "londonBlock", "mergeNetsplitBlock", "shanghaiTime", "cancunTime", "pragueTime", "osakaTime" })
        {
            config[fork] = 0;
        }

        config["terminalTotalDifficulty"] = 0;
        config["terminalTotalDifficultyPassed"] = true;
        config["rollupX"] = new JsonObject
        {
            ["blockIntervalSeconds"] = 1,
            ["syncIntervalBlocks"] = 5,
            ["l1Reference"] = new JsonObject
            {
                ["chainId"] = 10200,
                ["number"] = 23_318_297,
                ["hash"] = ReferenceHash.ToString(),
                ["timestamp"] = ReferenceTimestamp,
                ["prevRandao"] = ReferenceRandao.ToString(),
            },
        };
        JsonObject genesis = new()
        {
            ["config"] = config,
            ["timestamp"] = $"0x{ReferenceTimestamp + 10:x}",
            ["mixHash"] = ReferenceRandao.ToString(),
            ["gasLimit"] = "0x1c9c380",
            ["alloc"] = new JsonObject
            {
                ["0x0000F90827F1C53a10cb7A02335B175320002935"] = new JsonObject { ["balance"] = "0x0", ["nonce"] = "0x1", ["code"] = HistoryCode },
            },
        };
        mutate?.Invoke(genesis);
        return JsonDocument.Parse(genesis.ToJsonString()).RootElement;
    }
}
