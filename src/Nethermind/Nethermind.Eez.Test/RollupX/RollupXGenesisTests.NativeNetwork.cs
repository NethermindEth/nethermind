// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.IO;
using Nethermind.Crypto;
using Nethermind.Eez.Attester;
using Nethermind.Eez.Execution;
using Nethermind.Logging;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public partial class RollupXGenesisTests
{
    [TestCase("profile-collector-a", (byte)0x11)]
    [TestCase("profile-collector-b", (byte)0x12)]
    public void NativeNetwork_IndependentVector_MatchesStaticAbiAndCommitment(string id, byte collector)
    {
        NativeOfflineProfile.Inputs input = NetworkInput(collector);
        JsonElement vector = OfflineVector(id, 7);
        NativeOfflineProfile.Validate(in input, [], input.L1ChainId, input.HistoryCodeHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(NativeNetworkProfile.Encode(in input), Is.EqualTo(Bytes.FromHexString(vector.GetProperty("expectedBytes").GetString()!)));
            Assert.That(NativeNetworkProfile.Compute(in input), Is.EqualTo(new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!)));
            Assert.That(NativeOfflineProfile.Compute(in input, []), Is.EqualTo(NativeNetworkProfile.Compute(in input)));
        }
    }

    [Test]
    public void NativeNetwork_Key_MatchesIndependentDomainAndDeploymentVector(
        [Values("verification-key-base", "verification-key-different-profile", "verification-key-different-registry",
            "verification-key-different-proof-system", "verification-key-different-attester")] string id)
    {
        JsonElement vector = OfflineVector(id, 7);
        JsonElement args = vector.GetProperty("arguments");
        ValueHash256 network = new(args[1].GetString()!);
        ValueHash256 key = NativeNetworkProfile.VerificationKey(in network, new Address(args[2].GetString()!),
            new Address(args[3].GetString()!), new Address(args[4].GetString()!));
        Assert.That(key, Is.EqualTo(new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!)));
    }

    [Test]
    public void NativeNetwork_CoreContext_MatchesIndependentTwoWordEncoding(
        [Values("core-context-actual-parent", "core-context-historical")] string id)
    {
        JsonElement vector = OfflineVector(id, 7);
        JsonElement args = vector.GetProperty("arguments");
        L1PostingTarget target = new(ulong.Parse(args[0].GetString()!, CultureInfo.InvariantCulture), new Hash256(args[1].GetString()!));
        byte[] data = target.CustomData();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(data, Has.Length.EqualTo(64));
            Assert.That(data, Is.EqualTo(Bytes.FromHexString(vector.GetProperty("expectedBytes").GetString()!)));
            Assert.That(ValueKeccak.Compute(data), Is.EqualTo(new ValueHash256(vector.GetProperty("expectedKeccak256").GetString()!)));
        }
    }

    [Test]
    public void NativeNetwork_Context_DoesNotBindUnsignedRootOrRuleEpoch([Values] bool actualParent)
    {
        NativeOfflineProfile.Inputs profile = NetworkInput(0x11);
        NativeL1Header endpoint = ContextEndpoint();
        NativeEvidenceAnchor anchor = new(endpoint.Number, endpoint.Hash, actualParent ? 132UL : null);
        ValueHash256 first = OfflineHash(0x66), second = OfflineHash(0x67);
        NativeOfflineContext left = new(in profile, [0, 125], ContextRegistry, ContextManager, 7, in first, in anchor, endpoint);
        NativeOfflineContext right = new(in profile, [], ContextRegistry, ContextManager, 7, in second, in anchor, endpoint);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(left.UsesCore, Is.True);
            Assert.That(left.CustomData.ToArray(), Is.EqualTo(right.CustomData.ToArray()));
            Assert.That(left.CustomData.ToArray(), Is.EqualTo(new L1PostingTarget(actualParent ? 132UL : 0, new Hash256(endpoint.Hash)).CustomData()));
        }
    }

    [Test]
    public void NativeNetwork_CompatibleForkCrossing_RetainsHistoricalAnchorChecks([Values] bool current)
    {
        NativeOfflineProfile.Inputs profile = current ? NetworkInput(0x11) : OfflineInput(0x11) with { ValidationVersion = 6, HistoryCodeHash = NativeProxyHistory.RuntimeHash };
        NativeL1Header endpoint = ContextEndpoint();
        NativeEvidenceAnchor anchor = new(endpoint.Number, endpoint.Hash, null);
        ValueHash256 accepted = OfflineHash(0x66);
        NativeOfflineContext context = new(in profile, [0, 125], ContextRegistry, ContextManager, 7, in accepted, in anchor, endpoint);
        NativeL1Header inclusion = NativeL1Header.Decode(L1HeaderBytes(endpoint.Number + 1, 132, endpoint.Hash), LegacyHeaderRules);
        if (current) Assert.That(() => context.ValidateInclusion(inclusion), Throws.Nothing);
        else Assert.That(() => context.ValidateInclusion(inclusion), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void NativeNetwork_Finalizer_RequiresTheLocallyDerivedKeyBeforeSigning(
        [Values("matching", "key", "registry", "proof-system", "signer")] string field)
    {
        const string capture = "rollupx/native-offline-v7";
        NativeGenesisSnapshot genesis = NativeGenesisSnapshot.Load(File.ReadAllBytes(StatelessFixtures.PathOf(capture, "l2-genesis.json")), NativeSystemTxFormat.ContractV7);
        NativeL1Rules l1 = NativeL1Rules.LoadEthereum(File.ReadAllBytes(StatelessFixtures.PathOf(capture, "l1-genesis.json")));
        NativeStreamingProfile profile = new(genesis, l1);
        JsonElement identity = StatelessFixtures.ReadJson(capture, "identity.json");
        Address registry = field == "registry" ? ContextRegistry : new Address(identity.GetProperty("registry_address").GetString()!);
        Address proofSystem = field == "proof-system" ? ContextRegistry : new Address(identity.GetProperty("proof_system").GetString()!);
        ValueHash256 keyValue = field == "key" ? OfflineHash(0x99) : new ValueHash256(identity.GetProperty("vkey").GetString()!);
        using PrivateKey key = new(field == "signer" ? "0000000000000000000000000000000000000000000000000000000000000001"
            : "8b3a350cf5c34c9194ca85829a2df0ec3153be0318b5e2d3348e872092edffba");
        EezSettlementContext context = new(identity.GetProperty("rollup_id").GetUInt64(), genesis.Specs.ChainId, proofSystem, keyValue,
            genesis.Rules.Schedule!.BlockIntervalSeconds);
        Address manager = new(identity.GetProperty("rollup_manager_address").GetString()!);
        if (field == "matching")
            Assert.That(() => new NativeOfflineFinalizer(profile, context, registry, manager, new EezAttestationSigner(key), LimboLogs.Instance), Throws.Nothing);
        else
            Assert.That(() => new NativeOfflineFinalizer(profile, context, registry, manager, new EezAttestationSigner(key), LimboLogs.Instance), Throws.TypeOf<ArgumentException>());
    }

    private static NativeOfflineProfile.Inputs NetworkInput(byte collector) => OfflineInput(collector) with
    {
        ValidationVersion = 7,
        HistoryCodeHash = NativeProxyHistory.RuntimeHash,
    };
}
