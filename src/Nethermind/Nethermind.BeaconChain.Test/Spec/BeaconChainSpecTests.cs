// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Autofac;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.BeaconChain.Test.Spec;

public class BeaconChainSpecTests
{
    [Test]
    public void A_configuration_copy_keeps_separate_identity()
    {
        BeaconChainSpec original = BeaconChainSpec.Mainnet;
        BeaconChainSpec copy = original with { };

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(copy == original, Is.False);
        Assert.That(copy.Equals((object)original), Is.False);
        Assert.That(copy.GetHashCode(), Is.EqualTo(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(copy)));
        Assert.That(copy.ToString(), Is.EqualTo(typeof(BeaconChainSpec).FullName));
    }

    [TestCaseSource(nameof(ShippedNetworks))]
    public void The_execution_chain_id_selects_the_network(string name, ulong chainId, BeaconChainSpec spec)
    {
        BeaconChainSpec resolved = BeaconChainSpec.ForExecution(ExecutionSchedules.ForChainId(chainId));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(resolved.ChainId, Is.EqualTo(chainId), name);
        Assert.That(resolved.GenesisValidatorsRoot, Is.EqualTo(spec.GenesisValidatorsRoot), name);
    }

    [TestCase(BlockchainIds.Holesky)]
    [TestCase(0ul)]
    public void An_unsupported_execution_chain_id_fails_loudly(ulong chainId)
    {
        UnsupportedBeaconNetworkException ex = Assert.Throws<UnsupportedBeaconNetworkException>(
            () => BeaconChainSpec.ForExecution(ExecutionSchedules.ForChainId(chainId)))!;

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ex.ChainId, Is.EqualTo(chainId));
        Assert.That(ex.Message, Does.Contain(chainId.ToString()),
            "the offending chain id must be visible in the failure so an operator can diagnose it");
    }

    // Independent sources: each network's published consensus config.yaml (eth-clients/{hoodi,sepolia}, gnosischain/configs,
    // consensus-specs mainnet); do not derive expectations from the execution chainspecs the specs are now resolved from.
    [TestCase("foundation", BlockchainIds.Mainnet, 1606824023ul,
        "0x00000000@0 0x01000000@74240 0x02000000@144896 0x03000000@194048 0x04000000@269568 0x05000000@364032 0x06000000@411392",
        "412672:15 419072:21", 9ul, "0x07000000")]
    [TestCase("hoodi", BlockchainIds.Hoodi, 1742213400ul,
        "0x10000910@0 0x20000910@0 0x30000910@0 0x40000910@0 0x50000910@0 0x60000910@2048 0x70000910@50688",
        "52480:15 54016:21", 9ul, "0x07000000")]
    [TestCase("sepolia", BlockchainIds.Sepolia, 1655733600ul,
        "0x90000069@0 0x90000070@50 0x90000071@100 0x90000072@56832 0x90000073@132608 0x90000074@222464 0x90000075@272640 0x90000076@353024",
        "274176:15 275712:21", 9ul, "0x90000076")]
    [TestCase("gnosis", BlockchainIds.Gnosis, 1638993340ul,
        "0x00000064@0 0x01000064@512 0x02000064@385536 0x03000064@648704 0x04000064@889856 0x05000064@1337856 0x06000064@1714688",
        "", 2ul, "0x00000000")]
    [TestCase("chiado", BlockchainIds.Chiado, 1665396300ul,
        "0x0000006f@0 0x0100006f@90 0x0200006f@180 0x0300006f@244224 0x0400006f@516608 0x0500006f@948224 0x0600006f@1353216",
        "", 2ul, "0x00000000")]
    public void Fork_timing_from_the_execution_schedule_matches_the_published_consensus_config(
        string chainspec, ulong chainId, ulong genesisTime, string forks, string blobSchedule, ulong maxBlobsPerBlockElectra, string gloasVersion)
    {
        string[] expectedForks = forks.Split(' ');
        string[] expectedBlobs = blobSchedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ISpecProvider[] sources = [ExecutionSchedules.ForChainId(chainId), ExecutionSchedules.FromChainspec(chainspec)];

        using IDisposable scope = Assert.EnterMultipleScope();
        foreach (ISpecProvider source in sources)
        {
            BeaconChainSpec spec = BeaconChainSpec.ForExecution(source);
            string from = source is ChainSpecBasedSpecProvider ? $"chainspec/{chainspec}.json" : source.GetType().Name;
            Assert.That(spec.GenesisTime, Is.EqualTo(genesisTime), from);
            Assert.That(spec.Forks.Select(static f => $"{f.Version.ToHexString(withZeroX: true)}@{f.Epoch}"), Is.EqualTo(expectedForks), from);
            Assert.That(spec.BlobSchedule.Select(static b => $"{b.Epoch}:{b.MaxBlobsPerBlock}"), Is.EqualTo(expectedBlobs), from);
            Assert.That(spec.ElectraForkEpoch, Is.EqualTo(spec.Forks[5].Epoch), from);
            Assert.That(spec.FuluForkEpoch, Is.EqualTo(spec.Forks[6].Epoch), from);
            Assert.That(spec.GloasForkEpoch, Is.EqualTo(spec.Forks.Length > 7 ? spec.Forks[7].Epoch : Presets.FarFutureEpoch), from);
            Assert.That(spec.MaxBlobsPerBlockElectra, Is.EqualTo(maxBlobsPerBlockElectra), from);
            Assert.That(spec.GloasForkVersion.ToHexString(withZeroX: true), Is.EqualTo(gloasVersion), from);
        }
    }

    // Independent sources: eth-clients/hoodi config.yaml and genesis_validators_root.txt; do not derive expectations from BeaconChainSpec.
    [Test]
    public void Hoodi_spec_matches_its_published_network_parameters()
    {
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(BeaconChainSpec.Hoodi.ChainId, Is.EqualTo(BlockchainIds.Hoodi));
        Assert.That(BeaconChainSpec.Hoodi.GenesisValidatorsRoot.ToString(),
            Is.EqualTo("0x212f13fc4df078b6cb7db228f1c8307566dcecf900867401a92023d7ba99cb5f"));
        Assert.That(BeaconChainSpec.Hoodi.GenesisTime, Is.EqualTo(1742213400ul));
        Assert.That(BeaconChainSpec.Hoodi.SecondsPerSlot, Is.EqualTo(12ul));
        Assert.That(BeaconChainSpec.Hoodi.SlotsPerEpoch, Is.EqualTo(32ul));
        Assert.That(BeaconChainSpec.Hoodi.ElectraForkEpoch, Is.EqualTo(2048ul));
        Assert.That(BeaconChainSpec.Hoodi.FuluForkEpoch, Is.EqualTo(50688ul));
        Assert.That(BeaconChainSpec.Hoodi.MaxBlobsPerBlockElectra, Is.EqualTo(9ul));
        Assert.That(BeaconChainSpec.Hoodi.GloasForkEpoch, Is.EqualTo(Presets.FarFutureEpoch));
        Assert.That(BeaconChainSpec.Hoodi.Forks, Has.Length.EqualTo(7));
        Assert.That(BeaconChainSpec.Hoodi.BlobSchedule, Has.Length.EqualTo(2));
    }

    // Independent sources: eth-clients/sepolia metadata, public beacon genesis/fork_schedule, Lighthouse/Prysm config; Gloas parameters: ethereum/pm#2205 and lodestar#10119.
    [Test]
    public void Sepolia_spec_matches_its_published_network_parameters()
    {
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(BeaconChainSpec.Sepolia.ChainId, Is.EqualTo(BlockchainIds.Sepolia));
        Assert.That(BeaconChainSpec.Sepolia.GenesisValidatorsRoot.ToString(),
            Is.EqualTo("0xd8ea171f3c94aea21ebc42a1ed61052acf3f9209c00e4efbaaddac09ed9b8078"));
        Assert.That(BeaconChainSpec.Sepolia.GenesisTime, Is.EqualTo(1655733600ul));
        Assert.That(BeaconChainSpec.Sepolia.SecondsPerSlot, Is.EqualTo(12ul));
        Assert.That(BeaconChainSpec.Sepolia.SlotsPerEpoch, Is.EqualTo(32ul));
        Assert.That(BeaconChainSpec.Sepolia.ElectraForkEpoch, Is.EqualTo(222464ul));
        Assert.That(BeaconChainSpec.Sepolia.FuluForkEpoch, Is.EqualTo(272640ul));
        Assert.That(BeaconChainSpec.Sepolia.GloasForkEpoch, Is.EqualTo(353024ul));
        Assert.That(BeaconChainSpec.Sepolia.MaxBlobsPerBlockElectra, Is.EqualTo(9ul));
        Assert.That(BeaconChainSpec.Sepolia.Forks, Has.Length.EqualTo(8),
            "Sepolia has a confirmed Gloas entry");
        Assert.That(BeaconChainSpec.Sepolia.BlobSchedule, Has.Length.EqualTo(2));
    }

    [TestCaseSource(nameof(ShippedNetworks))]
    public void Every_shipped_spec_carries_bootnodes(string name, ulong chainId, BeaconChainSpec spec) =>
        Assert.That(spec.Bootnodes, Is.Not.Empty, $"{name} ({chainId}) has no bootnode records");

    // Independent source: a Lighthouse node of the Kurtosis devnet these files were generated for advertised fork digest 0x3b5bd9ca in its ENR.
    [Test]
    public void A_network_config_file_defines_a_network_the_chain_id_does_not([Values] bool throughContainer)
    {
        BeaconChainSpec spec = ForDevnet(DevnetConfig, writeGenesisState: true, chainspecGenesisTime: null, throughContainer: throughContainer);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(spec.ChainId, Is.EqualTo(DevnetChainId));
        Assert.That(spec.GenesisTime, Is.EqualTo(DevnetGenesisTime));
        Assert.That(spec.GenesisValidatorsRoot.ToString(), Is.EqualTo(DevnetGenesisValidatorsRoot));
        Assert.That(spec.Forks.Select(static f => $"{f.Version.ToHexString(withZeroX: true)}@{f.Epoch}"),
            Is.EqualTo(new[] { "0x10000038@0", "0x20000038@0", "0x30000038@0", "0x40000038@0", "0x50000038@0", "0x60000038@0", "0x70000038@0" }));
        Assert.That(spec.GloasForkEpoch, Is.EqualTo(Presets.FarFutureEpoch));
        Assert.That(spec.GloasForkVersion.ToHexString(withZeroX: true), Is.EqualTo("0x80000038"));
        Assert.That(spec.GetBlobParameters(0), Is.EqualTo(new BlobScheduleEntry(0, 21)));
        Assert.That(ForkDigest.Compute(spec, 0), Is.EqualTo(Bytes.FromHexString("0x3b5bd9ca")));
        Assert.That(spec.Bootnodes, Is.Empty);
        Assert.That(spec.CheckpointSyncUrl, Is.Null);
    }

    [Test]
    public void A_network_config_file_may_carry_unscheduled_blob_schedule_entries() =>
        Assert.That(ForDevnet(DevnetConfig.Replace("GAS_LIMIT_SCHEDULE: []", "  - EPOCH: 18446744073709551615\n    MAX_BLOBS_PER_BLOCK: 32\nGAS_LIMIT_SCHEDULE: []"), writeGenesisState: true, chainspecGenesisTime: null)
            .GetBlobParameters(1000), Is.EqualTo(new BlobScheduleEntry(0, 21)));

    [TestCase("PRESET_BASE: 'mainnet'", "PRESET_BASE: 'minimal'", "PRESET_BASE")]
    [TestCase("SLOT_DURATION_MS: 12000", "SLOT_DURATION_MS: 6000", "SLOT_DURATION_MS")]
    [TestCase("SHARD_COMMITTEE_PERIOD: 256", "SHARD_COMMITTEE_PERIOD: 64", "SHARD_COMMITTEE_PERIOD")]
    [TestCase("CONFIRMATION_BYZANTINE_THRESHOLD: 25", "CONFIRMATION_BYZANTINE_THRESHOLD: 33", "CONFIRMATION_BYZANTINE_THRESHOLD")]
    [TestCase("DEPOSIT_CHAIN_ID: 3151908", "DEPOSIT_CHAIN_ID: 1", "DEPOSIT_CHAIN_ID")]
    [TestCase("FULU_FORK_EPOCH: 0", "FULU_FORK_EPOCH: 1", "FULU_FORK_EPOCH")]
    [TestCase("GLOAS_FORK_EPOCH: 18446744073709551615", "GLOAS_FORK_EPOCH: 10", "GLOAS_FORK_EPOCH")]
    [TestCase("MAX_BLOBS_PER_BLOCK: 21", "MAX_BLOBS_PER_BLOCK: 15", "BLOB_SCHEDULE")]
    [TestCase("- EPOCH: 0", "- EPOCH: 18446744073709551615", "BLOB_SCHEDULE")]
    [TestCase("GENESIS_FORK_VERSION: 0x10000038", "", "GENESIS_FORK_VERSION")]
    [TestCase("ALTAIR_FORK_EPOCH: 0", "ALTAIR_FORK_EPOCH: soon", "ALTAIR_FORK_EPOCH")]
    [TestCase("MAX_BLOBS_PER_BLOCK_ELECTRA: 9", "MAX_BLOBS_PER_BLOCK_ELECTRA: 9\nMAX_REQUEST_PAYLOADS: 64", "MAX_REQUEST_PAYLOADS")]
    [TestCase("  - EPOCH: 0\n    MAX_BLOBS_PER_BLOCK: 21", "  - 21", "not a readable consensus config.yaml")]
    public void A_network_config_file_this_node_cannot_run_fails_startup_naming_the_key(string line, string replacement, string key) =>
        Assert.That(() => ForDevnet(DevnetConfig.Replace(line, replacement), writeGenesisState: true, chainspecGenesisTime: null),
            Throws.InstanceOf<InvalidConfigurationException>().With.Message.Contains(key));

    [TestCase(false, null, "genesis.ssz")]
    [TestCase(true, DevnetGenesisTime + 12, "beaconChainGenesisTimestamp")]
    public void The_genesis_state_beside_a_network_config_file_must_exist_and_match_the_chainspec(bool writeGenesisState, ulong? chainspecGenesisTime, string reason) =>
        Assert.That(() => ForDevnet(DevnetConfig, writeGenesisState, chainspecGenesisTime),
            Throws.InstanceOf<InvalidConfigurationException>().With.Message.Contains(reason));

    [Test]
    public void A_network_config_file_accepts_an_uppercase_hex_prefix() =>
        Assert.That(ForDevnet(DevnetConfig.Replace("0x", "0X"), writeGenesisState: true, chainspecGenesisTime: null).Forks[0].Version,
            Is.EqualTo(Bytes.FromHexString("0x10000038")));

    [Test]
    public void A_duplicate_Gloas_version_from_the_file_names_the_fork()
    {
        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(() => ForDevnet(
            DevnetConfig.Replace("0x80000038", "0x70000038").Replace("GLOAS_FORK_EPOCH: 18446744073709551615", "GLOAS_FORK_EPOCH: 0"),
            writeGenesisState: true, chainspecGenesisTime: null, scheduleAmsterdam: true))!;

        Assert.That(ex.Message, Does.Contain("Gloas fork version 0x70000038 repeats").And.Not.Contain("BeaconChain.GloasForkVersion"));
    }

    private const ulong DevnetChainId = 3151908;
    private const ulong DevnetGenesisTime = 1795524678;
    private const string DevnetGenesisValidatorsRoot = "0xba5d167db90ff012d3677f64ff126795260e25aed9487220539c0527dba7de6b";

    /// <summary>The keys of a Kurtosis devnet <c>config.yaml</c> this release reads, and some it ignores even though they differ from mainnet.</summary>
    private const string DevnetConfig = """
        PRESET_BASE: 'mainnet'
        CONFIG_NAME: 'testnet' # ignored
        MIN_GENESIS_ACTIVE_VALIDATOR_COUNT: 160
        MIN_GENESIS_TIME: 1795524678
        GENESIS_FORK_VERSION: 0x10000038
        GENESIS_DELAY: 0
        ALTAIR_FORK_VERSION: 0x20000038
        ALTAIR_FORK_EPOCH: 0
        BELLATRIX_FORK_VERSION: 0x30000038
        BELLATRIX_FORK_EPOCH: 0
        CAPELLA_FORK_VERSION: 0x40000038
        CAPELLA_FORK_EPOCH: 0
        DENEB_FORK_VERSION: 0x50000038
        DENEB_FORK_EPOCH: 0
        ELECTRA_FORK_VERSION: 0x60000038
        ELECTRA_FORK_EPOCH: 0
        FULU_FORK_VERSION: 0x70000038
        FULU_FORK_EPOCH: 0
        GLOAS_FORK_VERSION: 0x80000038
        GLOAS_FORK_EPOCH: 18446744073709551615
        SLOT_DURATION_MS: 12000
        SHARD_COMMITTEE_PERIOD: 256
        INCLUSION_LIST_DUE_BPS: 7500
        DEPOSIT_CHAIN_ID: 3151908
        DEPOSIT_CONTRACT_ADDRESS: 0x00000000219ab540356cBB839Cbe05303d7705Fa
        MAX_BLOBS_PER_BLOCK_ELECTRA: 9
        BLOB_SCHEDULE:
          - EPOCH: 0
            MAX_BLOBS_PER_BLOCK: 21
        GAS_LIMIT_SCHEDULE: []
        CONFIRMATION_BYZANTINE_THRESHOLD: 25
        """;

    /// <summary>The execution side of the devnet: a geth genesis activating every fork, both BPO forks included, at genesis.</summary>
    private const string DevnetGenesis = """
        {
          "config": {
            "chainId": 3151908, "homesteadBlock": 0, "eip150Block": 0, "eip155Block": 0, "eip158Block": 0, "byzantiumBlock": 0,
            "constantinopleBlock": 0, "petersburgBlock": 0, "istanbulBlock": 0, "berlinBlock": 0, "londonBlock": 0, "mergeNetsplitBlock": 0,
            "terminalTotalDifficulty": 0, "shanghaiTime": 0, "cancunTime": 0, "pragueTime": 0, "osakaTime": 0, "bpo1Time": 0, "bpo2Time": 0,
            "depositContractAddress": "0x00000000219ab540356cBB839Cbe05303d7705Fa",
            "blobSchedule": {
              "cancun": { "target": 3, "max": 6, "baseFeeUpdateFraction": 3338477 },
              "prague": { "target": 6, "max": 9, "baseFeeUpdateFraction": 5007716 },
              "bpo1": { "target": 10, "max": 15, "baseFeeUpdateFraction": 8346193 },
              "bpo2": { "target": 14, "max": 21, "baseFeeUpdateFraction": 11684671 }
            }
          },
          "alloc": {}, "coinbase": "0x0000000000000000000000000000000000000000", "difficulty": "0x0", "extraData": "", "gasLimit": "0x3938700",
          "nonce": "0x1234", "mixhash": "0x0000000000000000000000000000000000000000000000000000000000000000",
          "parentHash": "0x0000000000000000000000000000000000000000000000000000000000000000", "timestamp": "1795524678"
        }
        """;

    private static BeaconChainSpec ForDevnet(string config, bool writeGenesisState, ulong? chainspecGenesisTime, bool throughContainer = false, bool scheduleAmsterdam = false)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string configFile = Path.Combine(directory.Path, "config.yaml");
        File.WriteAllText(configFile, config);
        if (writeGenesisState)
            File.WriteAllBytes(Path.Combine(directory.Path, "genesis.ssz"), Bytes.FromHexString($"0x4688056b00000000{DevnetGenesisValidatorsRoot[2..]}"));

        JsonObject genesis = JsonNode.Parse(DevnetGenesis)!.AsObject();
        if (chainspecGenesisTime is not null) genesis["config"]!["beaconChainGenesisTimestamp"] = chainspecGenesisTime;
        if (scheduleAmsterdam) genesis["config"]!["amsterdamTime"] = 0;
        using MemoryStream genesisJson = new(Encoding.UTF8.GetBytes(genesis.ToJsonString()));
        ChainSpec chainSpec = new AutoDetectingChainSpecLoader(new EthereumJsonSerializer(), LimboLogs.Instance).Load(genesisJson);
        if (throughContainer)
        {
            using IContainer container = BeaconChainTestContainer.Builder(
                config: new BeaconChainConfig { NetworkConfigFile = configFile },
                execution: new ChainSpecBasedSpecProvider(chainSpec), chainSpec: chainSpec).Build();
            return container.Resolve<BeaconChainSpec>();
        }
        return BeaconChainSpec.ForNetworkConfig(configFile, new ChainSpecBasedSpecProvider(chainSpec), null, chainSpec.Parameters.BlobSchedule?.Select(static entry => entry.Timestamp));
    }

    private static IEnumerable<object[]> ShippedNetworks() =>
    [
        ["Mainnet", BlockchainIds.Mainnet, BeaconChainSpec.Mainnet],
        ["Hoodi", BlockchainIds.Hoodi, BeaconChainSpec.Hoodi],
        ["Sepolia", BlockchainIds.Sepolia, BeaconChainSpec.Sepolia],
        ["Gnosis", BlockchainIds.Gnosis, BeaconChainSpec.Gnosis],
        ["Chiado", BlockchainIds.Chiado, BeaconChainSpec.Chiado],
    ];
}
