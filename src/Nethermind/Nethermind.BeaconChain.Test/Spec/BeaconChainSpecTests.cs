// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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
        "0x10000910@0 0x20000910@0 0x30000910@0 0x40000910@0 0x50000910@0 0x60000910@2048 0x70000910@50688 0x80000910@143616",
        "52480:15 54016:21", 9ul, "0x80000910")]
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
        Assert.That(BeaconChainSpec.Hoodi.GloasForkEpoch, Is.EqualTo(143616ul));
        Assert.That(BeaconChainSpec.Hoodi.Forks, Has.Length.EqualTo(8));
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

    private static IEnumerable<object[]> ShippedNetworks() =>
    [
        ["Mainnet", BlockchainIds.Mainnet, BeaconChainSpec.Mainnet],
        ["Hoodi", BlockchainIds.Hoodi, BeaconChainSpec.Hoodi],
        ["Sepolia", BlockchainIds.Sepolia, BeaconChainSpec.Sepolia],
        ["Gnosis", BlockchainIds.Gnosis, BeaconChainSpec.Gnosis],
        ["Chiado", BlockchainIds.Chiado, BeaconChainSpec.Chiado],
    ];
}
