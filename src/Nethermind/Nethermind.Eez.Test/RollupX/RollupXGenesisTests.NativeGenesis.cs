// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    private const string NativeGenesisFixture = "rollupx/native-profile-v1";
    private static readonly string[] InvalidNativeGenesisFields =
    [
        "bare", "extension", "offset-missing", "offset-range", "offset-overflow", "offset-type", "collector-missing", "collector-zero", "collector-system",
        "collector-manager", "unknown-top", "unknown-config", "unknown-extension", "unknown-reference", "unknown-account", "unknown-blob",
        "parent", "number", "gas", "blob-gas", "difficulty", "extra-data", "mix", "fork", "base-fee", "chain-zero", "bootstrap",
        "history-missing", "history-code", "history-nonce", "history-balance", "history-slot", "history-extra", "duplicate-account", "duplicate-slot",
        "duplicate-ordinary-slot", "dao-type", "unknown-eip150-hash"
    ];

    [Test]
    public void Load_NativeUpstreamGenesis_BindsCompleteSourceAndExecutionProfile()
    {
        byte[] input = File.ReadAllBytes(StatelessFixtures.PathOf(NativeGenesisFixture, "genesis.json"));
        NativeGenesisSnapshot snapshot = NativeGenesisSnapshot.Load(input);
        ValueHash256 digest = ValueKeccak.Compute(RollupXCanonicalJson.Encode(input).Span);
        Array.Clear(input);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.SourceDigest, Is.EqualTo(digest));
            Assert.That(snapshot.Rules.UsesNativeSystemTransactions, Is.True);
            Assert.That(snapshot.Specs.ChainId, Is.EqualTo(696990));
            Assert.That(snapshot.Rules.FeeCollector, Is.EqualTo(new Address("0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee0078")));
            Assert.That(snapshot.Rules.Schedule!.GenesisTimestamp, Is.EqualTo(12));
            Assert.That((snapshot.History.Interval, snapshot.History.Delay, snapshot.History.BootstrapCutoff), Is.EqualTo((12UL, 6UL, 0UL)));
            Assert.That(snapshot.GenesisHash, Is.Not.EqualTo(default(ValueHash256)));
        }
    }

    [Test]
    public void Compute_RegisteredGenesis_ReproducesIndependentStateRootAndBlockHash()
    {
        using FileStream stream = File.OpenRead(StatelessFixtures.PathOf("rollupx/eezdev-699069", "genesis.json"));
        ChainSpec spec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(stream);
        Hash256 hash = EezGenesisHash.Compute(spec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.Genesis!.StateRoot, Is.EqualTo(new Hash256("0x8255029550a16e602838f463ca67bfc560ff9841d576905e2412844de258c18e")));
            Assert.That(hash, Is.EqualTo(new Hash256("0x05227a1af51a988231b625b276c6b29594f0a93090efd93463934cd1bf6e606e")));
        }
    }

    [Test]
    public void Load_OrdinaryAllocationChanges_ChangesGenesisCommitment([Values("balance", "nonce", "code", "storage")] string field)
    {
        JsonObject document = NativeGenesisDocument();
        NativeGenesisSnapshot before = NativeGenesisSnapshot.Load(NativeGenesisBytes(document));
        JsonObject account = new() { ["balance"] = "0x7", ["nonce"] = "0x0", ["code"] = "0x", ["storage"] = new JsonObject() };
        document["alloc"]!["0x1111111111111111111111111111111111111111"] = account;
        NativeGenesisSnapshot added = NativeGenesisSnapshot.Load(NativeGenesisBytes(document));
        switch (field)
        {
            case "balance": account[field] = "0x8"; break;
            case "nonce": account[field] = "0x1"; break;
            case "code": account[field] = "0x6000"; break;
            case "storage": account[field] = new JsonObject { ["0x01"] = "0x07" }; break;
        }
        NativeGenesisSnapshot changed = NativeGenesisSnapshot.Load(NativeGenesisBytes(document));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(added.GenesisHash, Is.Not.EqualTo(before.GenesisHash));
            Assert.That(changed.GenesisHash, Is.Not.EqualTo(added.GenesisHash));
            Assert.That(changed.SourceDigest, Is.Not.EqualTo(added.SourceDigest));
        }
    }

    [Test]
    public void Load_MixHashAlias_KeepsOriginalDocumentIdentity([Values] bool retainCanonical)
    {
        JsonObject document = NativeGenesisDocument();
        NativeGenesisSnapshot canonical = NativeGenesisSnapshot.Load(NativeGenesisBytes(document));
        document["mixhash"] = document["mixHash"]!.DeepClone();
        if (!retainCanonical) document.Remove("mixHash");
        NativeGenesisSnapshot alias = NativeGenesisSnapshot.Load(NativeGenesisBytes(document));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alias.GenesisHash, Is.EqualTo(canonical.GenesisHash));
            Assert.That(alias.SourceDigest, Is.Not.EqualTo(canonical.SourceDigest));
        }
        document["mixHash"] = "0x" + new string('0', 64);
        document["mixhash"] = "0x" + new string('1', 64);
        Assert.Throws<InvalidDataException>(() => NativeGenesisSnapshot.Load(NativeGenesisBytes(document)));
    }

    [Test]
    public void Load_NativeGenesis_LeavesLegacyActivationClosed()
    {
        using JsonDocument document = JsonDocument.Parse(NativeGenesisBytes(NativeGenesisDocument()));
        Assert.Throws<InvalidDataException>(() => EezChainRules.FromGenesis(document.RootElement));
    }

    [Test]
    public void Load_DuplicateJson_RejectsBeforeTypedLoader()
    {
        string text = Encoding.UTF8.GetString(NativeGenesisBytes(NativeGenesisDocument()));
        text = text.Replace("\"historyUpdateOffset\":2", "\"historyUpdateOffset\":2,\"historyUpdate\\u004fffset\":2", StringComparison.Ordinal);
        Assert.That(text, Does.Contain("\\u004f"), "precondition: duplicate decoded property was inserted");
        Assert.Throws<InvalidDataException>(() => NativeGenesisSnapshot.Load(Encoding.UTF8.GetBytes(text)));
    }

    [Test]
    public void Load_InvalidNativeGenesis_RejectsWithoutNormalization([ValueSource(nameof(InvalidNativeGenesisFields))] string field)
    {
        JsonObject document = NativeGenesisDocument();
        JsonObject section = Section(document);
        JsonObject history = document["alloc"]![EezConstants.L1HistoryAddress.ToString()]!.AsObject();
        switch (field)
        {
            case "bare": document = document["config"]!.DeepClone().AsObject(); break;
            case "extension": section["l1Reference"] = null; break;
            case "offset-missing": section.Remove("historyUpdateOffset"); break;
            case "offset-range": section["historyUpdateOffset"] = 6; break;
            case "offset-overflow": section["historyUpdateOffset"] = 4294967298UL; break;
            case "offset-type": section["historyUpdateOffset"] = "2"; break;
            case "collector-missing": section.Remove("feeCollector"); break;
            case "collector-zero": section["feeCollector"] = Address.Zero.ToString(); break;
            case "collector-system": section["feeCollector"] = Address.SystemUser.ToString(); break;
            case "collector-manager": section["feeCollector"] = EezConstants.RollupXEezl2Address.ToString(); break;
            case "unknown-top": document["gasLmit"] = "0x1c9c380"; break;
            case "unknown-config": document["config"]!["futureFork"] = 0; break;
            case "dao-type": document["config"]!["daoForkSupport"] = new JsonArray(); break;
            case "unknown-eip150-hash": document["config"]!["eip150Hash"] = Keccak.Zero.ToString(); break;
            case "unknown-extension": section["historyOffset"] = 2; break;
            case "unknown-reference": Reference(document)["finalized"] = true; break;
            case "unknown-account": history["constructor"] = "0x00"; break;
            case "unknown-blob": document["config"]!["blobSchedule"] = new JsonObject { ["cancun"] = new JsonObject { ["mx"] = 6 } }; break;
            case "parent": document["parentHash"] = ReferenceHash.ToString(); break;
            case "number": document["number"] = "0x1"; break;
            case "gas": document["gasLimit"] = "0x1c9c381"; break;
            case "blob-gas": document["blobGasUsed"] = "0x1"; break;
            case "difficulty": document["difficulty"] = "0x1"; break;
            case "extra-data": document["extraData"] = "0x" + new string('1', 66); break;
            case "mix": document["mixHash"] = ReferenceRandao.ToString(); break;
            case "fork": document["config"]!["osakaTime"] = 1; break;
            case "base-fee": document["baseFeePerGas"] = "0x1"; break;
            case "chain-zero": document["config"]!["chainId"] = 0; break;
            case "bootstrap": Reference(document)["timestamp"] = 12; break;
            case "history-missing": document["alloc"]!.AsObject().Remove(EezConstants.L1HistoryAddress.ToString()); break;
            case "history-code": history["code"] = "0x00"; break;
            case "history-nonce": history["nonce"] = "0x0"; break;
            case "history-balance": history["balance"] = "0x1"; break;
            case "history-slot": history["storage"]!["0x" + new string('0', 63) + "5"] = "0x7"; break;
            case "history-extra": history["storage"]!["0x07"] = "0x0"; break;
            case "duplicate-account": document["alloc"]![EezConstants.L1HistoryAddress.ToString()[2..]] = history.DeepClone(); break;
            case "duplicate-slot": history["storage"]!["0x1"] = "0x0"; break;
            case "duplicate-ordinary-slot":
                document["alloc"]!["0x1111111111111111111111111111111111111111"] = new JsonObject
                {
                    ["balance"] = "0x1",
                    ["storage"] = new JsonObject { ["0x01"] = "0x01", ["0x0001"] = "0x02" }
                };
                break;
        }
        Assert.Throws<InvalidDataException>(() => NativeGenesisSnapshot.Load(NativeGenesisBytes(document)));
    }

    private static JsonObject NativeGenesisDocument() => JsonNode.Parse(File.ReadAllBytes(StatelessFixtures.PathOf(NativeGenesisFixture, "genesis.json")))!.AsObject();
    private static byte[] NativeGenesisBytes(JsonObject document) => JsonSerializer.SerializeToUtf8Bytes(document);
}
