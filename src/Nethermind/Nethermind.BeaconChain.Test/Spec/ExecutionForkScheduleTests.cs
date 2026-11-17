// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text.Json.Nodes;
using Autofac;
using Autofac.Core;
using Nethermind.BeaconChain.Spec;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.BeaconChain.Test.Spec;

/// <summary>The beacon fork timing follows the execution chainspec, so an operator changes a fork date in one place.</summary>
public class ExecutionForkScheduleTests
{
    private const ulong MainnetGloasEpoch = 500_000ul;

    private static ulong TimeAt(BeaconChainSpec spec, ulong epoch) => spec.GenesisTime + epoch * spec.SlotsPerEpoch * spec.SecondsPerSlot;

    private static ISpecProvider Sepolia(Action<JsonObject> editConfig) =>
        ExecutionSchedules.FromChainspec("sepolia", json => editConfig(json["config"]!.AsObject()));

    private static ISpecProvider MainnetWithAmsterdamAt(ulong epoch) =>
        ExecutionSchedules.FromChainspec("foundation", json => json["config"]!["amsterdamTime"] = TimeAt(BeaconChainSpec.Mainnet, epoch));

    [Test]
    public void Gloas_moves_with_amsterdam_in_the_chainspec()
    {
        BeaconChainSpec spec = BeaconChainSpec.ForExecution(Sepolia(config => config["amsterdamTime"] = TimeAt(BeaconChainSpec.Sepolia, 400_000)));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(spec.GloasForkEpoch, Is.EqualTo(400_000ul));
        Assert.That(spec.Forks, Has.Length.EqualTo(BeaconChainSpec.Sepolia.Forks.Length), "the Gloas entry moves instead of being added twice");
        Assert.That(spec.ForkAtEpoch(353_024), Is.EqualTo(BeaconFork.Fulu), "the shipped epoch no longer activates Gloas");
        Assert.That(spec.ForkAtEpoch(400_000), Is.EqualTo(BeaconFork.Gloas));
        Assert.That(spec.VersionForEpoch(400_000), Is.EqualTo(BeaconChainSpec.Sepolia.GloasForkVersion));
        Assert.That(ForkDigest.Compute(spec, 353_024), Is.EqualTo(ForkDigest.Compute(spec, 353_023)));
    }

    [Test]
    public void Gloas_is_unscheduled_when_the_chainspec_drops_amsterdam()
    {
        BeaconChainSpec spec = BeaconChainSpec.ForExecution(Sepolia(config => config.Remove("amsterdamTime")));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(spec.GloasForkEpoch, Is.EqualTo(Presets.FarFutureEpoch));
        Assert.That(spec.Forks, Has.Length.EqualTo(BeaconChainSpec.Sepolia.Forks.Length - 1));
        Assert.That(spec.ForkAtEpoch(10_000_000), Is.EqualTo(BeaconFork.Fulu));
    }

    [Test]
    public void Amsterdam_on_a_network_without_a_published_gloas_version_needs_the_version_from_config()
    {
        ISpecProvider execution = MainnetWithAmsterdamAt(MainnetGloasEpoch);

        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(() => BeaconChainSpec.ForExecution(execution))!;
        BeaconChainSpec spec = BeaconChainSpec.ForExecution(execution, "0x07000000");

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(ex.Message, Does.Contain("set BeaconChain.GloasForkVersion"));
        Assert.That(spec.ForkAtEpoch(MainnetGloasEpoch - 1), Is.EqualTo(BeaconFork.Fulu));
        Assert.That(spec.ForkAtEpoch(MainnetGloasEpoch), Is.EqualTo(BeaconFork.Gloas));
        Assert.That(ForkDigest.Compute(spec, MainnetGloasEpoch - 1), Is.EqualTo(ForkDigest.Compute(BeaconChainSpec.Mainnet, MainnetGloasEpoch - 1)));
        // Same independently computed digest as the synthetic Gloas spec in GloasForkScheduleTests.
        Assert.That(ForkDigest.Compute(spec, MainnetGloasEpoch), Is.EqualTo(Bytes.FromHexString("0xce2153ed")));
    }

    [Test]
    public void Configured_version_replaces_the_published_one()
    {
        BeaconChainSpec spec = BeaconChainSpec.ForExecution(SepoliaSpecProvider.Instance, "0x90000099");

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(spec.GloasForkVersion, Is.EqualTo(Bytes.FromHexString("0x90000099")));
        Assert.That(spec.VersionForEpoch(353_024), Is.EqualTo(spec.GloasForkVersion));
        Assert.That(ForkDigest.Compute(spec, 353_024), Is.Not.EqualTo(ForkDigest.Compute(BeaconChainSpec.Sepolia, 353_024)));
    }

    [TestCase("0x80000910", "has no effect", TestName = "Version_without_amsterdam_has_no_effect")]
    [TestCase("0x9000", "not 4 bytes of hex")]
    [TestCase("0x900000991", "not 4 bytes of hex")]
    [TestCase("0x9000009g", "not 4 bytes of hex")]
    [TestCase("", "not 4 bytes of hex")]
    [TestCase("0x90000075", "repeats an earlier fork version")]
    public void Invalid_gloas_version_fails_with_a_message_naming_the_key(string version, string expected)
    {
        ISpecProvider execution = expected == "has no effect" ? MainnetSpecProvider.Instance : SepoliaSpecProvider.Instance;

        InvalidConfigurationException ex = AssertConflict(() => BeaconChainSpec.ForExecution(execution, version));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(ex.Message, Does.Contain("BeaconChain.GloasForkVersion"));
        Assert.That(ex.Message, Does.Contain(expected));
    }

    [Test]
    public void A_fork_off_an_epoch_boundary_is_refused([Values("osakaTime", "bpo1Time", "amsterdamTime")] string fork)
    {
        ISpecProvider execution = Sepolia(config => config[fork] = config[fork]!.GetValue<ulong>() + BeaconChainSpec.Sepolia.SecondsPerSlot);

        Assert.That(AssertConflict(() => BeaconChainSpec.ForExecution(execution)).Message, Does.Contain("not on a beacon epoch boundary"));
    }

    [Test]
    public void A_chainspec_without_a_beacon_genesis_time_is_refused() =>
        Assert.That(AssertConflict(() => BeaconChainSpec.ForExecution(Sepolia(config => config.Remove("beaconChainGenesisTimestamp")))).Message,
            Does.Contain("beaconChainGenesisTimestamp"));

    [Test]
    public void Amsterdam_before_osaka_is_refused() =>
        Assert.That(Assert.Throws<InvalidDataException>(() => Sepolia(config => config["amsterdamTime"] = config["osakaTime"]!.GetValue<ulong>() - 384))!.Message,
            Does.Contain("Unsupported fork ordering"));

    // The fork digest hashes each entry's epoch, so a change of target or update fraction alone still needs an entry.
    [TestCase(16, 24, 13_000_000, TestName = "Every_blob_parameter_changes")]
    [TestCase(20, 21, 11_684_671, TestName = "Only_the_target_changes")]
    [TestCase(14, 21, 13_000_000, TestName = "Only_the_update_fraction_changes")]
    public void A_blob_parameter_fork_after_osaka_adds_a_blob_schedule_entry(int target, int max, int fraction)
    {
        BeaconChainSpec spec = BeaconChainSpec.ForExecution(WithBpo3(target, max, fraction));

        Assert.That(spec.BlobSchedule, Is.EqualTo(new BlobScheduleEntry[] { new(274_176, 15), new(275_712, 21), new(300_000, (ulong)max) }));
    }

    // The consensus BLOB_SCHEDULE keeps such an entry, and get_blob_parameters returns its epoch into the fork digest.
    [Test]
    public void A_blob_parameter_only_fork_that_repeats_the_parameters_still_adds_an_entry()
    {
        ChainSpec chainSpec = ExecutionSchedules.LoadChainspec("sepolia", json => AddBpo3(json["config"]!.AsObject(), 14, 21, 11_684_671));
        using IContainer container = BeaconChainTestContainer.Builder(BlockchainIds.Sepolia, execution: new ChainSpecBasedSpecProvider(chainSpec), chainSpec: chainSpec).Build();

        BeaconChainSpec spec = container.Resolve<BeaconChainSpec>();

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(spec.BlobSchedule, Is.EqualTo(new BlobScheduleEntry[] { new(274_176, 15), new(275_712, 21), new(300_000, 21) }));
        Assert.That(ForkDigest.Compute(spec, 300_000), Is.Not.EqualTo(ForkDigest.Compute(spec, 299_999)));
    }

    // The consensus BLOB_SCHEDULE has no entry at a hard fork that keeps the blob parameters.
    [TestCase("osaka", 6, 9, 5_007_716)]
    [TestCase("amsterdam", 14, 21, 11_684_671)]
    public void A_hard_fork_that_lists_unchanged_blob_parameters_adds_no_entry(string fork, int target, int max, int fraction)
    {
        ChainSpec chainSpec = ExecutionSchedules.LoadChainspec("sepolia", json =>
            json["config"]!["blobSchedule"]![fork] = new JsonObject { ["target"] = target, ["max"] = max, ["baseFeeUpdateFraction"] = fraction });

        BeaconChainSpec spec = BeaconChainSpec.ForExecution(new ChainSpecBasedSpecProvider(chainSpec),
            blobScheduleTimestamps: chainSpec.Parameters.BlobSchedule!.Select(static entry => entry.Timestamp));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(chainSpec.Parameters.BlobSchedule!.Select(static entry => entry.Timestamp), Does.Contain(TimeAt(BeaconChainSpec.Sepolia, fork == "osaka" ? 272_640ul : 353_024ul)), "the chainspec lists the fork's blob parameters");
        Assert.That(spec.BlobSchedule, Is.EqualTo(BeaconChainSpec.Sepolia.BlobSchedule));
    }

    [Test]
    public void An_execution_only_fork_after_osaka_leaves_the_beacon_schedule_unchanged()
    {
        const ulong rewriteTime = 0x70000000;
        ChainSpec chainSpec = ExecutionSchedules.LoadChainspec("gnosis", json => json["params"]!["eip7939TransitionTimestamp"] = $"0x{rewriteTime:x}");
        ISpecProvider execution = new ChainSpecBasedSpecProvider(chainSpec);

        BeaconChainSpec spec = BeaconChainSpec.ForExecution(execution, blobScheduleTimestamps: chainSpec.Parameters.BlobSchedule!.Select(static entry => entry.Timestamp));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(execution.TransitionActivations.Select(static activation => activation.Timestamp), Does.Contain(rewriteTime), "the execution layer has a fork there");
        Assert.That(spec.Forks.Select(static fork => fork.Epoch), Is.EqualTo(BeaconChainSpec.Gnosis.Forks.Select(static fork => fork.Epoch)));
        Assert.That(spec.BlobSchedule, Is.Empty);
    }

    [Test]
    public void A_blob_parameter_fork_before_osaka_is_refused() =>
        Assert.That(Assert.Throws<InvalidDataException>(() => Sepolia(config => config["bpo1Time"] = config["osakaTime"]!.GetValue<ulong>() - 384))!.Message,
            Does.Contain("Unsupported fork ordering"));

    [Test]
    public void Amsterdam_on_a_gnosis_network_is_refused([Values("gnosis", "chiado")] string chainspec)
    {
        ISpecProvider execution = ExecutionSchedules.FromChainspec(chainspec, json => json["params"]!["eip7928TransitionTimestamp"] = "0x70000000");

        Assert.That(AssertConflict(() => BeaconChainSpec.ForExecution(execution)).Message, Does.Contain("Gnosis Gloas preset"));
    }

    [Test]
    public void Config_version_reaches_the_spec_the_driver_runs_on()
    {
        using IContainer container = BeaconChainTestContainer.Builder(
            BlockchainIds.Mainnet,
            config: new BeaconChainConfig { GloasForkVersion = "0x80000910" },
            execution: MainnetWithAmsterdamAt(MainnetGloasEpoch)).Build();

        BeaconChainSpec spec = container.Resolve<BeaconChainSpec>();

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(spec.ChainId, Is.EqualTo(BlockchainIds.Mainnet));
        Assert.That(spec.ForkAtEpoch(MainnetGloasEpoch), Is.EqualTo(BeaconFork.Gloas));
        Assert.That(spec.GloasForkVersion, Is.EqualTo(Bytes.FromHexString("0x80000910")));
    }

    [Test]
    public void Invalid_config_stops_start_up_with_the_configuration_error()
    {
        using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig { GloasForkVersion = "0x08000000" }).Build();

        DependencyResolutionException wrapped = Assert.Throws<DependencyResolutionException>(() => container.Resolve<BeaconChainSpec>())!;

        Assert.That(wrapped.GetBaseException(), Is.TypeOf<InvalidConfigurationException>().With.Message.Contains("has no effect"));
    }

    private static ISpecProvider WithBpo3(int target, int max, int fraction) => Sepolia(config => AddBpo3(config, target, max, fraction));

    private static void AddBpo3(JsonObject config, int target, int max, int fraction)
    {
        config.Remove("amsterdamTime");
        config["bpo3Time"] = TimeAt(BeaconChainSpec.Sepolia, 300_000);
        config["blobSchedule"]!["bpo3"] = new JsonObject { ["target"] = target, ["max"] = max, ["baseFeeUpdateFraction"] = fraction };
    }

    // Startup surfaces only InvalidConfigurationException as the operator's message and the configuration exit code.
    private static InvalidConfigurationException AssertConflict(Func<BeaconChainSpec> resolve)
    {
        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(() => resolve())!;
        Assert.That(ex.ExitCode, Is.EqualTo(ExitCodes.ConflictingConfigurations));
        return ex;
    }
}
