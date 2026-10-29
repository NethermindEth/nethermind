// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Follower.RollupX;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    private const string ChiadoCapture = "rollupx/chiado-l1";

    [Test]
    public void Load_ChiadoPreset_MatchesIndependentRulesWithoutChangingProcessBlobFees()
    {
        UInt256 minimum = Eip4844Constants.MinBlobGasPrice;
        NativeL1Rules rules = NativeL1Rules.Load("chiado");
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(ChiadoCapture, "profile.json")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.GenesisHash, Is.EqualTo(new ValueHash256(expected.RootElement.GetProperty("genesisHash").GetString()!)),
                "the native profile commits the execution-envelope genesis");
            Assert.That(rules.ConfigHash, Is.EqualTo(new ValueHash256(expected.RootElement.GetProperty("configHash").GetString()!)),
                "forks, genesis and blob rules must match the independently derived profile");
            Assert.That(rules.Specs.ChainId, Is.EqualTo(BlockchainIds.Chiado));
            Assert.That(rules.MaximumHistoricalBlobCount, Is.EqualTo(2));
            Assert.That(Eip4844Constants.MinBlobGasPrice, Is.EqualTo(minimum), "L1 selection must not change L2 blob fees");
            Assert.That(rules.RuleTimes.ToArray(), Is.EqualTo(new ulong[] { 0, ChiadoSpecProvider.ShanghaiTimestamp,
                ChiadoSpecProvider.CancunTimestamp, ChiadoSpecProvider.PragueTimestamp, ChiadoSpecProvider.OsakaTimestamp }));
        }
    }

    [Test]
    public void Load_ChiadoPreset_BindsNativeBlobLimitsAndFeeCollector(
        [Values(ChiadoSpecProvider.CancunTimestamp, ChiadoSpecProvider.PragueTimestamp, ChiadoSpecProvider.OsakaTimestamp)] ulong timestamp,
        [Values] bool before)
    {
        NativeL1Rules rules = NativeL1Rules.Load("chiado");
        ulong at = before ? timestamp - 1 : timestamp;
        IReleaseSpec spec = rules.Specs.GetSpec(23_731_489, at);
        ulong expected = at < ChiadoSpecProvider.CancunTimestamp ? 0UL : 2UL;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.PostingBlobLimit(at, 23_731_489), Is.EqualTo(expected));
            Assert.That(spec.FeeCollector, Is.EqualTo(ChiadoSpecProvider.FeeCollector), "native L1 fees retain the Chiado collector");
            Assert.That(spec.IsEip4844FeeCollectorEnabled, Is.EqualTo(at >= ChiadoSpecProvider.PragueTimestamp));
            if (at >= ChiadoSpecProvider.PragueTimestamp)
                Assert.That(spec.DepositContractAddress, Is.EqualTo(Eip6110Constants.ChiadoDepositContractAddress));
            if (expected != 0)
                Assert.That((spec.TargetBlobCount, spec.MaxBlobCount, spec.BlobBaseFeeUpdateFraction), Is.EqualTo((1UL, 2UL, 1_112_826UL)));
        }
    }

    [Test]
    public void Load_ChiadoPreset_RecordedRpcHeadersPreserveCanonicalHashes([Values(23_731_489UL, 23_731_531UL)] ulong number)
    {
        NativeL1Rules rules = NativeL1Rules.Load("chiado");
        using JsonDocument headers = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(ChiadoCapture, "headers.json")));
        BlockHeaderForRpc header = new EthereumJsonSerializer().Deserialize<BlockHeaderForRpc>(headers.RootElement.GetProperty(number.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetRawText())!;
        Assert.That(NativeL1Rpc.Decode(header, number, rules.Headers).Hash, Is.EqualTo(header.Hash!.ValueHash256),
            "the actual public post-merge header must decode without rewriting its fields");
    }

    [Test]
    public void Load_ChiadoPreset_PreMergeAuraHeader_Rejects()
    {
        NativeL1Rules rules = NativeL1Rules.Load("chiado");
        using JsonDocument headers = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(ChiadoCapture, "headers.json")));
        BlockHeaderForRpc header = new EthereumJsonSerializer().Deserialize<BlockHeaderForRpc>(headers.RootElement.GetProperty("0").GetRawText())!;
        Assert.That(() => NativeL1Rpc.Decode(header, 0, rules.Headers), Throws.TypeOf<EezSettlementException>(),
            "selecting Chiado must not accept pre-merge seals as post-merge evidence");
    }

    [Test]
    public void Profile_ChiadoPublicDeployment_MatchesRegisteredVerificationKey()
    {
        NativeL1Rules rules = NativeL1Rules.Load("chiado");
        NativeGenesisSnapshot genesis = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(ChiadoCapture, "genesis.json")), NativeSystemTxFormat.ContractV7);
        NativeStreamingProfile profile = new(genesis, rules);
        NativeOfflineProfile.Inputs network = profile.Offline;
        ValueHash256 commitment = NativeNetworkProfile.Compute(in network);
        ValueHash256 key = NativeNetworkProfile.VerificationKey(in commitment,
            new Address("0x6258189d3a04eb31a196a8931077dfe001a9f6fd"),
            new Address("0x1fb73b0954026b91d9771a378ccd6b0c71100d4c"),
            new Address("0x72a5ed7Ab535F5760a77D1770A49CF212e20bC3b"));
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(ChiadoCapture, "profile.json")));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(genesis.GenesisHash, Is.EqualTo(new ValueHash256("0x283b471b65276c23867b5351fe1973fc9438310d865b6f833d1cd6666478ccb0")));
            Assert.That(commitment, Is.EqualTo(new ValueHash256(expected.RootElement.GetProperty("networkProfileHash").GetString()!)));
            Assert.That(key, Is.EqualTo(new ValueHash256(expected.RootElement.GetProperty("verificationKey").GetString()!)),
                "the independently derived key was read back from the deployed manager");
        }
    }

    [Test]
    public void Profile_ChiadoBootstrapBeforeMerge_Rejects()
    {
        JsonNode document = JsonNode.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(ChiadoCapture, "genesis.json")))!;
        document["config"]!["rollupX"]!["l1Reference"]!["number"] = 680_929UL;
        NativeGenesisSnapshot genesis = NativeGenesisSnapshot.Load(JsonSerializer.SerializeToUtf8Bytes(document), NativeSystemTxFormat.ContractV7);
        Assert.That(() => NativeL1Rules.Load("chiado").Profile(genesis), Throws.TypeOf<InvalidDataException>(),
            "L2 bootstrap must follow the actual Chiado merge, not an assumed genesis transition");
    }

    [Test]
    public void NativeFollower_ChiadoPreset_ResolvesTheProductionL1HistoryServices()
    {
        NativeGenesisSnapshot genesis = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(ChiadoCapture, "genesis.json")), NativeSystemTxFormat.ContractV7);
        EezConfig config = new()
        {
            Enabled = true,
            FollowerEnabled = true,
            L1RpcUrl = "http://l1.test/",
            L1BeaconUrl = "http://beacon.test/",
            L1ChainId = BlockchainIds.Chiado,
            L1ChainConfigPath = "chiado",
            RegistryAddress = "0x6258189d3a04eb31a196a8931077dfe001a9f6fd",
            RegistryDeployBlock = 23_731_528,
            RollupId = 2,
        };
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .AddSingleton<IEezConfig>(config)
            .AddSingleton(Substitute.For<IEngineRpcModule>())
            .AddModule(new EezModule(config, genesis.Rules))
            .Build();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.Resolve<NativeL1Rpc>().Rules.Specs.ChainId, Is.EqualTo(BlockchainIds.Chiado));
            Assert.That(container.Resolve<NativeCanonicalHistory>(), Is.Not.Null);
        }
    }

    [Test]
    public void StreamingRuntime_ChiadoPreset_StartsOnlyForTheSelectedNetworkAndSigner([Values] bool wrongKey)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        Directory.CreateDirectory(directory.Path);
        string chainPath = Path.Combine(directory.Path, "l2.json");
        File.Copy(StatelessFixtures.PathOf(ChiadoCapture, "genesis.json"), chainPath);
        (ISpecProvider specs, EezChainRules rules) = ChainConfigLoader.LoadProfile(chainPath);
        using X509Certificate2 server = StreamingEndpointTests.EndpointServer.Certificate("server", true);
        using X509Certificate2 composer = StreamingEndpointTests.EndpointServer.Certificate("composer", false);
        File.WriteAllBytes(Path.Combine(directory.Path, "server.pfx"), server.Export(X509ContentType.Pkcs12, "test"));
        File.WriteAllBytes(Path.Combine(directory.Path, "composer.cer"), composer.RawData);
        File.WriteAllText(Path.Combine(directory.Path, "password"), "test");
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(ChiadoCapture, "profile.json")));
        ValueHash256 network = new(expected.RootElement.GetProperty("networkProfileHash").GetString()!);
        Address registry = new("0x6258189d3a04eb31a196a8931077dfe001a9f6fd");
        Address manager = new("0xf321dbb2e5ad31d3154da951a0257a38a7867ba7");
        Address proofSystem = new("0x1fb73b0954026b91d9771a378ccd6b0c71100d4c");
        EezAttestationSigner signer = new(TestItem.PrivateKeyA);
        ValueHash256 verificationKey = NativeNetworkProfile.VerificationKey(in network, registry, proofSystem, signer.Address);
        if (wrongKey)
        {
            byte[] changed = verificationKey.Bytes.ToArray();
            changed[0] ^= 1;
            verificationKey = new ValueHash256(changed);
        }
        EezSettlementContext context = new(2, specs.ChainId, proofSystem, verificationKey, 1);
        string settings = Path.Combine(directory.Path, "streaming.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new
        {
            profile = "rollupx-native", listenAddress = "127.0.0.1:0", l1ChainId = BlockchainIds.Chiado,
            l1ChainConfigPath = "chiado", l2GenesisHash = "0x283b471b65276c23867b5351fe1973fc9438310d865b6f833d1cd6666478ccb0",
            registry = registry.ToString(), manager = manager.ToString(), serverCertificate = "server.pfx",
            serverPasswordFile = "password", composerCertificates = new[] { "composer.cer" },
        }));
        WindowLimits limits = new(64, 64 * 1024 * 1024, 1_000_000);
        AttesterOptions options = new(new IPEndPoint(IPAddress.Loopback, 0), chainPath, context.RollupId, context.VerificationKey,
            signer.Address, proofSystem, "", "", 1, context.GasLimit, limits, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), settings);
        AttestationPipeline pipeline = new(specs, context, signer, LimboLogs.Instance);
        if (wrongKey)
            Assert.That(() => StreamingRuntime.Load(options, specs, pipeline, signer, LimboLogs.Instance, rules), Throws.TypeOf<FormatException>(),
                "the prover must reject a key that differs from the independently selected network and local signer");
        else
        {
            using StreamingRuntime runtime = StreamingRuntime.Load(options, specs, pipeline, signer, LimboLogs.Instance, rules);
            Assert.That(runtime.Options.L1ChainConfigPath, Is.EqualTo("chiado"), "the production prover loads the preset without treating it as a file");
        }
    }

    [Test]
    public void Load_L1GenesisFile_PreservesEthereumProfile()
    {
        using TempPath file = TempPath.GetTempFile();
        byte[] bytes = L1Bytes(L1Document());
        File.WriteAllBytes(file.Path, bytes);
        Assert.That(NativeL1Rules.Load(file.Path).ConfigHash, Is.EqualTo(NativeL1Rules.LoadEthereum(bytes).ConfigHash));
    }

    [Test]
    public void Load_L1GenesisExceedingSizeLimit_RejectsBeforeParsing()
    {
        using TempPath file = TempPath.GetTempFile();
        using (FileStream stream = File.OpenWrite(file.Path)) stream.SetLength(8 * 1024 * 1024 + 1);
        Assert.That(() => NativeL1Rules.Load(file.Path), Throws.TypeOf<InvalidDataException>());
    }
}
