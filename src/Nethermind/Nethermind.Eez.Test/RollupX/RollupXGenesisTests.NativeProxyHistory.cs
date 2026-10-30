// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
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
    [Test]
    public void Load_ContractV6Genesis_BindsEmptyProxyAndIndependentStateRoot()
    {
        byte[] input = File.ReadAllBytes(StatelessFixtures.PathOf("rollupx/native-profile-v6", "genesis.json"));
        NativeGenesisSnapshot snapshot = NativeGenesisSnapshot.Load(input, NativeSystemTxFormat.ContractV6);
        using MemoryStream stream = new(input, writable: false);
        ChainSpec spec = new GethGenesisLoader(new EthereumJsonSerializer()).Load(stream);
        Hash256 genesis = EezGenesisHash.Compute(spec);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Rules.NativeFormat, Is.EqualTo(NativeSystemTxFormat.ContractV6));
            Assert.That(snapshot.Rules.UsesNativeContract, Is.True);
            Assert.That(spec.Genesis!.StateRoot, Is.EqualTo(new Hash256("0x688e9a795bb28a1eda48e66790feb60e4e7efffb73cc3bac981dbe6e302fcc5e")), "independent state root for the unchanged genesis fixture");
            Assert.That(snapshot.GenesisHash, Is.EqualTo(genesis.ValueHash256));
            Assert.That(NativeHistoryBlockExecutor.GetRuntimeHash(snapshot.Rules.NativeFormat), Is.EqualTo(NativeProxyHistory.RuntimeHash));
        }
    }

    [Test]
    public void Profile_ContractV6Snapshot_BindsVersionAndProxyRuntime()
    {
        NativeL1Rules l1 = NativeL1Rules.LoadEthereum(File.ReadAllBytes(StatelessFixtures.PathOf("rollupx/native-profile-v6", "l1-genesis.json")));
        NativeGenesisSnapshot l2 = NativeGenesisSnapshot.Load(
            File.ReadAllBytes(StatelessFixtures.PathOf("rollupx/native-profile-v6", "l2-genesis.json")), NativeSystemTxFormat.ContractV6);
        NativeOfflineProfile.Inputs profile = l1.Profile(l2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(profile.ValidationVersion, Is.EqualTo(6), "the adapter must select the same contract as its genesis snapshot");
            Assert.That(profile.HistoryCodeHash, Is.EqualTo(NativeProxyHistory.RuntimeHash));
            Assert.That(profile.GenesisHash, Is.EqualTo(l2.GenesisHash));
        }
    }

    [Test]
    public void Load_ContractV6HistoryAccountMutation_Rejects(
        [Values("proxy", "implementation", "admin")] string role,
        [Values("missing", "code", "nonce", "balance", "storage")] string field)
    {
        JsonObject document = ProxyGenesisDocument();
        Address address = ProxyAccountAddress(role);
        JsonObject account = ProxyGenesisAccount(document, address);
        switch (field)
        {
            case "missing": document["alloc"]!.AsObject().Remove(address.ToString()); break;
            case "code": account["code"] = "0x00"; break;
            case "nonce": account["nonce"] = role == "proxy" ? "0x1" : "0x2"; break;
            case "balance": account["balance"] = "0x1"; break;
            case "storage": account["storage"] = new JsonObject { ["0x01"] = "0x01" }; break;
        }
        Assert.That(() => NativeGenesisSnapshot.Load(NativeGenesisBytes(document), NativeSystemTxFormat.ContractV6), Throws.TypeOf<InvalidDataException>(), $"{role}/{field}");
    }

    [Test]
    public void Load_ContractV6ProxyPointersAndEmptyStore_RejectsMutation(
        [Values("implementation", "admin", "history-record", "endpoint")] string field)
    {
        JsonObject document = ProxyGenesisDocument();
        JsonObject storage = ProxyGenesisAccount(document, EezConstants.L1HistoryAddress)["storage"]!.AsObject();
        string key = field switch
        {
            "implementation" => "0x360894a13ba1a3210667c828492db98dca3e2076cc3735a920a3ca505d382bbc",
            "admin" => "0xb53127684a568b3173ae13b9f8a6016e243e63b6e8ee1178d6a717850b5d6103",
            "history-record" => "0x" + NativeHistoryBlockExecutor.HashSlot(1).ToString("x64"),
            _ => "0x01",
        };
        storage[key] = "0x01";
        Assert.That(() => NativeGenesisSnapshot.Load(NativeGenesisBytes(document), NativeSystemTxFormat.ContractV6), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Load_ContractV6UpgradeOwner_RejectsInvalidWord([Values("zero", "wide", "system", "slot")] string field)
    {
        JsonObject document = ProxyGenesisDocument();
        JsonObject storage = ProxyGenesisAccount(document, NativeProxyHistory.AdminAddress)["storage"]!.AsObject();
        storage.Clear();
        storage[field == "slot" ? "0x01" : "0x00"] = field switch
        {
            "zero" => "0x00",
            "wide" => "0x01" + new string('0', 40),
            "system" => Address.SystemUser.ToString(),
            _ => "0x01",
        };
        Assert.That(() => NativeGenesisSnapshot.Load(NativeGenesisBytes(document), NativeSystemTxFormat.ContractV6), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Load_ContractV6ChangedUpgradeOwner_ChangesIdentity()
    {
        JsonObject document = ProxyGenesisDocument();
        NativeGenesisSnapshot before = NativeGenesisSnapshot.Load(NativeGenesisBytes(document), NativeSystemTxFormat.ContractV6);
        JsonObject storage = ProxyGenesisAccount(document, NativeProxyHistory.AdminAddress)["storage"]!.AsObject();
        storage.Clear();
        storage["0x00"] = "0x1111111111111111111111111111111111111111";
        NativeGenesisSnapshot after = NativeGenesisSnapshot.Load(NativeGenesisBytes(document), NativeSystemTxFormat.ContractV6);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(after.GenesisHash, Is.Not.EqualTo(before.GenesisHash), "configured upgrade authority belongs to genesis identity");
            Assert.That(after.SourceDigest, Is.Not.EqualTo(before.SourceDigest));
        }
    }

    private static Address ProxyAccountAddress(string role) => role switch
    {
        "implementation" => NativeProxyHistory.ImplementationAddress,
        "admin" => NativeProxyHistory.AdminAddress,
        _ => EezConstants.L1HistoryAddress,
    };

    private static JsonObject ProxyGenesisDocument() => JsonNode.Parse(File.ReadAllBytes(StatelessFixtures.PathOf("rollupx/native-profile-v6", "genesis.json")))!.AsObject();

    private static JsonObject ProxyGenesisAccount(JsonObject document, Address address) => document["alloc"]![address.ToString()]!.AsObject();
}
