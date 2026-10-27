// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    private const string L1Capture = "rollupx/native-offline-bidirectional";
    private static readonly string[] InvalidL1Fields =
    [
        "bare", "missing-alloc", "unknown-top", "unknown-config", "rollup-extension", "chain-missing", "chain-zero", "chain-string",
        "gnosis", "chiado", "dao-flag", "merge-flag", "unknown-blob", "blob-missing", "blob-zero", "blob-target", "blob-string",
        "duplicate-updates", "number", "parent", "engine-field", "duplicate-account", "duplicate-slot"
    ];

    [Test]
    public void LoadEthereum_CapturedL1_ProducesIndependentGenesisRulesAndProfileHashes()
    {
        NativeL1Rules rules = NativeL1Rules.LoadEthereum(L1Bytes(L1Document()));
        NativeGenesisSnapshot l2 = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(L1Capture, "l2-genesis.json")));
        NativeOfflineProfile.Inputs profile = rules.Profile(l2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.GenesisHash, Is.EqualTo(new ValueHash256("0x9aaab55cf37d924af8638f6b50b0e238554efa215a55f62304c5853ebdbfe123")));
            Assert.That(rules.ConfigHash, Is.EqualTo(new ValueHash256("0x6dad1f7d24e1d34f08fda373214f8c5c6f5b1b37db79ee7df71fc4fd2d010027")));
            Assert.That(NativeOfflineProfile.Compute(in profile, rules.RuleTimes), Is.EqualTo(new ValueHash256("0x32442221f79225663e25bf29d8c7e48df1d1d2e13d9f41b01965683485b48972")));
            Assert.That(rules.Headers, Is.EqualTo(new NativeL1HeaderRules(0, 0, 0, 0, null)));
            Assert.That(rules.RuleTimes.ToArray(), Is.EqualTo(new ulong[] { 0 }));
            Assert.That(rules.SourceDigest, Is.Not.EqualTo(rules.ConfigHash), "source identity and effective rule identity have different domains");
        }
    }

    [Test]
    public void LoadEthereum_SourceOwnershipAndUnusedMetadata_PreserveEffectiveRules()
    {
        JsonObject document = L1Document();
        byte[] bytes = L1Bytes(document);
        NativeL1Rules before = NativeL1Rules.LoadEthereum(bytes);
        Array.Clear(bytes);
        document["config"]!["daoForkSupport"] = true;
        NativeL1Rules changed = NativeL1Rules.LoadEthereum(L1Bytes(document));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.ConfigHash, Is.EqualTo(before.ConfigHash), "no DAO activation is configured");
            Assert.That(changed.SourceDigest, Is.Not.EqualTo(before.SourceDigest), "complete source metadata remains committed");
            Assert.That(before.Specs.ChainId, Is.EqualTo(1337));
        }
    }

    [Test]
    public void LoadEthereum_BlobOverrides_UseEachForkDefaultsAndExplicitScheduledUpdates()
    {
        JsonObject document = L1Document();
        document["timestamp"] = "0x0";
        JsonObject config = document["config"]!.AsObject();
        config["pragueTime"] = 100;
        config["osakaTime"] = 200;
        config["bpo1Time"] = 300;
        config["bpo2Time"] = 400;
        config["blobSchedule"] = new JsonObject { ["cancun"] = Blob(2, 4, 4444444), ["bpo1"] = Blob(7, 11, 2222222) };
        NativeL1Rules rules = NativeL1Rules.LoadEthereum(L1Bytes(document));
        foreach ((ulong timestamp, ulong target, ulong max, ulong fraction) in new[]
        {
            (0UL, 2UL, 4UL, 4444444UL), (100UL, 6UL, 9UL, 5007716UL), (200UL, 6UL, 9UL, 5007716UL),
            (300UL, 7UL, 11UL, 2222222UL), (400UL, 7UL, 11UL, 2222222UL)
        })
        {
            IReleaseSpec spec = rules.Specs.GetSpec(1, timestamp);
            Assert.That((spec.TargetBlobCount, spec.MaxBlobCount, spec.BlobBaseFeeUpdateFraction), Is.EqualTo((target, max, fraction)), $"at {timestamp}");
        }
        Assert.That(rules.RuleTimes.ToArray(), Is.EqualTo(new ulong[] { 0, 100, 200, 300, 400 }));
    }

    [Test]
    public void LoadEthereum_NamedBlobUpdate_BindsItsParametersAndEpoch([Values("target", "max", "baseFeeUpdateFraction", "timestamp")] string field)
    {
        JsonObject document = L1Document();
        JsonObject config = document["config"]!.AsObject();
        config["bpo1Time"] = 2000000000;
        config["blobSchedule"] = new JsonObject { ["bpo1"] = Blob(7, 11, 2222222) };
        NativeL1Rules original = NativeL1Rules.LoadEthereum(L1Bytes(document));
        if (field == "timestamp") config["bpo1Time"] = 2000000001;
        else config["blobSchedule"]!["bpo1"]![field] = field switch { "target" => 8, "max" => 12, _ => 2222223 };
        NativeL1Rules changed = NativeL1Rules.LoadEthereum(L1Bytes(document));
        Assert.That(changed.ConfigHash, Is.Not.EqualTo(original.ConfigHash));
    }

    [Test]
    public void Profile_FutureBlockForkOrForeignChain_RejectsBootstrap([Values("block", "chain")] string field)
    {
        JsonObject document = L1Document();
        if (field == "block") document["config"]!["muirGlacierBlock"] = 10;
        else document["config"]!["chainId"] = 1338;
        NativeL1Rules rules = NativeL1Rules.LoadEthereum(L1Bytes(document));
        NativeGenesisSnapshot l2 = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(L1Capture, "l2-genesis.json")));
        Assert.Throws<InvalidDataException>(() => rules.Profile(l2));
    }

    [Test]
    public void LoadEthereum_UnsupportedHeaderForks_SelectEarliestAndKeepBothEpochs()
    {
        JsonObject document = L1Document();
        document["config"]!["amsterdamTime"] = 2000000000;
        document["config"]!["bogotaTime"] = 2000000010;
        NativeL1Rules rules = NativeL1Rules.LoadEthereum(L1Bytes(document));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.Headers.UnsupportedTime, Is.EqualTo(2000000000));
            Assert.That(rules.RuleTimes.ToArray(), Is.EqualTo(new ulong[] { 0, 2000000000, 2000000010 }));
            Assert.That(rules.Headers.Matches(1, 2000000000, 21), Is.False);
        }
    }

    [Test]
    public void LoadEthereum_HeaderForkCoordinates_RemainDistinct()
    {
        JsonObject document = L1Document();
        document["timestamp"] = "0x0";
        JsonObject config = document["config"]!.AsObject();
        config["londonBlock"] = 10;
        config["shanghaiTime"] = 100;
        config["cancunTime"] = 200;
        config["pragueTime"] = 300;
        config["osakaTime"] = 400;
        NativeL1Rules rules = NativeL1Rules.LoadEthereum(L1Bytes(document));
        Assert.That(rules.Headers, Is.EqualTo(new NativeL1HeaderRules(10, 100, 200, 300, null)));
    }

    [TestCase(2000000010UL, 2000000000UL, 2000000000UL)]
    [TestCase(2000000000UL, null, 2000000000UL)]
    [TestCase(null, 2000000000UL, 2000000000UL)]
    public void LoadEthereum_UnsupportedHeaderForks_CheckEachCoordinate(ulong? amsterdam, ulong? bogota, ulong earliest)
    {
        JsonObject document = L1Document();
        document["config"]!["amsterdamTime"] = amsterdam;
        document["config"]!["bogotaTime"] = bogota;
        Assert.That(NativeL1Rules.LoadEthereum(L1Bytes(document)).Headers.UnsupportedTime, Is.EqualTo(earliest));
    }

    [TestCase("prague", "pragueTime")]
    [TestCase("osaka", "osakaTime")]
    [TestCase("bpo1", "bpo1Time")]
    [TestCase("bpo2", "bpo2Time")]
    [TestCase("bpo3", "bpo3Time")]
    [TestCase("bpo4", "bpo4Time")]
    [TestCase("bpo5", "bpo5Time")]
    [TestCase("Amsterdam", "amsterdamTime")]
    public void LoadEthereum_EachExplicitBlobSchedule_BindsParametersAndEpoch(string key, string timeKey)
    {
        JsonObject document = L1Document();
        JsonObject config = document["config"]!.AsObject();
        config[timeKey] = 2000000000;
        if (key == "prague") config["osakaTime"] = 2000000010;
        config["blobSchedule"] = new JsonObject { [key] = Blob(7, 11, 2222222) };
        NativeL1Rules rules = NativeL1Rules.LoadEthereum(L1Bytes(document));
        IReleaseSpec at = rules.Specs.GetSpec(1, 2000000000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((at.TargetBlobCount, at.MaxBlobCount, at.BlobBaseFeeUpdateFraction), Is.EqualTo((7UL, 11UL, 2222222UL)));
            Assert.That(rules.RuleTimes.ToArray(), Does.Contain(2000000000UL));
        }
        config["blobSchedule"]![key]!["target"] = 8;
        Assert.That(NativeL1Rules.LoadEthereum(L1Bytes(document)).ConfigHash, Is.Not.EqualTo(rules.ConfigHash));
    }

    [Test]
    public void LoadEthereum_BlobSchedule_SortsByTimestampRatherThanForkName()
    {
        JsonObject document = L1Document();
        JsonObject config = document["config"]!.AsObject();
        config["bpo1Time"] = 2000000010;
        config["bpo2Time"] = 2000000000;
        config["blobSchedule"] = new JsonObject { ["bpo1"] = Blob(8, 12, 2222223), ["bpo2"] = Blob(7, 11, 2222222) };
        NativeL1Rules rules = NativeL1Rules.LoadEthereum(L1Bytes(document));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.RuleTimes.ToArray(), Is.EqualTo(new ulong[] { 0, 2000000000, 2000000010 }));
            Assert.That(rules.Specs.GetSpec(1, 2000000000).TargetBlobCount, Is.EqualTo(7));
            Assert.That(rules.Specs.GetSpec(1, 2000000010).TargetBlobCount, Is.EqualTo(8));
        }
    }

    [Test]
    public void LoadEthereum_PreLondonBaseFee_DoesNotChangeTheGenesisHeader()
    {
        JsonObject document = PreLondonL1();
        document["baseFeePerGas"] = "0x0";
        NativeL1Rules original = NativeL1Rules.LoadEthereum(L1Bytes(document));
        document["baseFeePerGas"] = "0x1";
        NativeL1Rules changed = NativeL1Rules.LoadEthereum(L1Bytes(document));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed.GenesisHash, Is.EqualTo(original.GenesisHash));
            Assert.That(changed.ConfigHash, Is.EqualTo(original.ConfigHash));
            Assert.That(changed.SourceDigest, Is.Not.EqualTo(original.SourceDigest));
        }
    }

    [Test]
    public void LoadEthereum_UnsupportedGenesisHeader_Rejects([Values("zero-london", "wide-base-fee", "missing-london")] string field)
    {
        JsonObject document = PreLondonL1();
        if (field == "zero-london")
        {
            document["config"]!["londonBlock"] = 0;
            document["baseFeePerGas"] = "0x0";
        }
        else if (field == "wide-base-fee") document["baseFeePerGas"] = "0x10000000000000000";
        else document["config"]!["shanghaiTime"] = 0;
        Assert.Throws<InvalidDataException>(() => NativeL1Rules.LoadEthereum(L1Bytes(document)));
    }

    [Test]
    public void LoadEthereum_MergeCoordinates_DistinguishActivationAndAdvertisedFork([Values] bool sepolia)
    {
        JsonObject document = L1Document();
        JsonObject config = document["config"]!.AsObject();
        config["chainId"] = sepolia ? BlockchainIds.Sepolia : BlockchainIds.Mainnet;
        config.Remove("mergeNetsplitBlock");
        config["terminalTotalDifficulty"] = sepolia ? "0x3c6568f12e8000" : "0xc70d808a128d7380000";
        NativeL1Rules rules = NativeL1Rules.LoadEthereum(L1Bytes(document));
        JsonElement golden = StatelessFixtures.ReadJson("rollupx/native-profile-v1", "merge-rule-vectors.json")[sepolia ? 1 : 0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rules.ConfigHash, Is.EqualTo(new ValueHash256(golden.GetProperty("configHash").GetString()!)));
            Assert.That(rules.Specs.TerminalTotalDifficulty, Is.EqualTo(sepolia ? SepoliaSpecProvider.Instance.TerminalTotalDifficulty : MainnetSpecProvider.Instance.TerminalTotalDifficulty));
        }
    }

    [Test]
    public void LoadEthereum_InvalidSchema_Rejects([ValueSource(nameof(InvalidL1Fields))] string field)
    {
        JsonObject document = L1Document();
        JsonObject config = document["config"]!.AsObject();
        switch (field)
        {
            case "bare": document = config.DeepClone().AsObject(); break;
            case "missing-alloc": document.Remove("alloc"); break;
            case "unknown-top": document["unknownField"] = 0; break;
            case "unknown-config": config["futureFork"] = 0; break;
            case "rollup-extension": config["rollupX"] = new JsonObject(); break;
            case "chain-missing": config.Remove("chainId"); break;
            case "chain-zero": config["chainId"] = 0; break;
            case "chain-string": config["chainId"] = "1337"; break;
            case "gnosis": config["chainId"] = BlockchainIds.Gnosis; break;
            case "chiado": config["chainId"] = BlockchainIds.Chiado; break;
            case "dao-flag": config["daoForkSupport"] = new JsonArray(); break;
            case "merge-flag": config["terminalTotalDifficultyPassed"] = 1; break;
            case "unknown-blob": config["blobSchedule"] = new JsonObject { ["futureFork"] = Blob(3, 6, 1) }; break;
            case "blob-missing": config["blobSchedule"] = new JsonObject { ["cancun"] = new JsonObject { ["target"] = 3, ["max"] = 6 } }; break;
            case "blob-zero": config["blobSchedule"] = new JsonObject { ["cancun"] = Blob(3, 6, 0) }; break;
            case "blob-target": config["blobSchedule"] = new JsonObject { ["cancun"] = Blob(7, 6, 1) }; break;
            case "blob-string": config["blobSchedule"] = new JsonObject { ["cancun"] = Blob(3, 6, 1) }; config["blobSchedule"]!["cancun"]!["target"] = "3"; break;
            case "duplicate-updates":
                config["bpo1Time"] = 2000000000; config["bpo2Time"] = 2000000000;
                config["blobSchedule"] = new JsonObject { ["bpo1"] = Blob(7, 11, 1), ["bpo2"] = Blob(8, 12, 2) };
                break;
            case "number": document["number"] = "0x1"; break;
            case "parent": document["parentHash"] = ReferenceHash.ToString(); break;
            case "engine-field": config["ethash"] = new JsonObject { ["period"] = 1 }; break;
            case "duplicate-account":
                document["alloc"]!["0x1111111111111111111111111111111111111111"] = new JsonObject { ["balance"] = "0x0" };
                document["alloc"]!["1111111111111111111111111111111111111111"] = new JsonObject { ["balance"] = "0x0" };
                break;
            case "duplicate-slot":
                document["alloc"]!["0x1111111111111111111111111111111111111111"] = new JsonObject
                {
                    ["balance"] = "0x0",
                    ["storage"] = new JsonObject { ["0x01"] = "0x01", ["0x0001"] = "0x02" }
                };
                break;
        }
        Assert.Throws<InvalidDataException>(() => NativeL1Rules.LoadEthereum(L1Bytes(document)));
    }

    private static JsonObject L1Document() => JsonNode.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(L1Capture, "l1-genesis.json")))!.AsObject();
    private static JsonObject PreLondonL1()
    {
        JsonObject document = L1Document();
        JsonObject config = document["config"]!.AsObject();
        foreach (string field in new[] { "londonBlock", "shanghaiTime", "cancunTime", "pragueTime", "osakaTime" }) config.Remove(field);
        return document;
    }
    private static byte[] L1Bytes(JsonObject value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static JsonObject Blob(ulong target, ulong max, ulong fraction) => new() { ["target"] = target, ["max"] = max, ["baseFeeUpdateFraction"] = fraction };
}
