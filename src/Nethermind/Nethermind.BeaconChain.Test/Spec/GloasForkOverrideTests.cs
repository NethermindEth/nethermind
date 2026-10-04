// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Reflection;
using Autofac;
using Autofac.Core;
using Nethermind.BeaconChain.Spec;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Spec;

/// <summary>
/// An operator following a network whose Gloas parameters moved after this release must be able to
/// say so in config, and a typo must stop the node rather than peer on the wrong fork digest.
/// </summary>
public class GloasForkOverrideTests
{
    private const ulong OverrideEpoch = 500_000ul;

    [Test]
    public void Override_moves_the_fork_and_rotates_the_digest_at_the_new_epoch()
    {
        BeaconChainSpec spec = BeaconChainSpec.Mainnet.WithGloasForkOverride(OverrideEpoch, "0x07000000");

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(BeaconChainSpec.Mainnet.ForkAtEpoch(OverrideEpoch), Is.EqualTo(BeaconFork.Fulu), "the built-in mainnet spec is not mutated");
        Assert.That(spec.ForkAtEpoch(OverrideEpoch - 1), Is.EqualTo(BeaconFork.Fulu));
        Assert.That(spec.ForkAtEpoch(OverrideEpoch), Is.EqualTo(BeaconFork.Gloas));
        Assert.That(spec.GloasForkEpoch, Is.EqualTo(OverrideEpoch));
        Assert.That(ForkDigest.Compute(spec, OverrideEpoch - 1), Is.EqualTo(ForkDigest.Compute(BeaconChainSpec.Mainnet, OverrideEpoch - 1)));
        // Same independently computed digest as the synthetic Gloas spec in GloasForkScheduleTests.
        Assert.That(ForkDigest.Compute(spec, OverrideEpoch), Is.EqualTo(Bytes.FromHexString("0xce2153ed")));
    }

    [Test]
    public void Override_of_a_scheduled_network_moves_its_fork_entry_instead_of_adding_a_second()
    {
        BeaconChainSpec spec = BeaconChainSpec.Sepolia.WithGloasForkOverride(400_000, null);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(spec.Forks, Has.Length.EqualTo(BeaconChainSpec.Sepolia.Forks.Length));
        Assert.That(spec.ForkAtEpoch(353_024), Is.EqualTo(BeaconFork.Fulu), "the built-in epoch no longer activates Gloas");
        Assert.That(spec.VersionForEpoch(400_000), Is.EqualTo(BeaconChainSpec.Sepolia.GloasForkVersion), "the built-in version is kept");
        Assert.That(ForkDigest.Compute(spec, 353_024), Is.EqualTo(ForkDigest.Compute(spec, 353_023)));
    }

    [Test]
    public void Override_version_changes_the_digest_and_the_state_fork_version()
    {
        BeaconChainSpec spec = BeaconChainSpec.Sepolia.WithGloasForkOverride(null, "0x90000099");

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(spec.GloasForkVersion, Is.EqualTo(Bytes.FromHexString("0x90000099")));
        Assert.That(spec.VersionForEpoch(353_024), Is.EqualTo(spec.GloasForkVersion));
        Assert.That(ForkDigest.Compute(spec, 353_024), Is.Not.EqualTo(ForkDigest.Compute(BeaconChainSpec.Sepolia, 353_024)));
    }

    [Test]
    public void Far_future_epoch_unschedules_gloas_on_a_scheduled_network()
    {
        BeaconChainSpec spec = BeaconChainSpec.Sepolia.WithGloasForkOverride(Presets.FarFutureEpoch, null);

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(spec.ForkAtEpoch(10_000_000), Is.EqualTo(BeaconFork.Fulu));
        Assert.That(spec.Forks, Has.Length.EqualTo(BeaconChainSpec.Sepolia.Forks.Length - 1));
    }

    [Test]
    public void Epoch_equal_to_the_fulu_epoch_is_allowed_and_activates_gloas_there()
    {
        ulong fulu = BeaconChainSpec.Sepolia.FuluForkEpoch;

        BeaconChainSpec spec = BeaconChainSpec.Sepolia.WithGloasForkOverride(fulu, "0x90000099");

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(spec.ForkAtEpoch(fulu - 1), Is.EqualTo(BeaconFork.Electra));
        Assert.That(spec.ForkAtEpoch(fulu), Is.EqualTo(BeaconFork.Gloas));
    }

    [Test]
    public void Overriding_an_override_keeps_every_earlier_fork()
    {
        BeaconChainSpec once = BeaconChainSpec.Sepolia.WithGloasForkOverride(BeaconChainSpec.Sepolia.FuluForkEpoch, "0x90000099");

        BeaconChainSpec twice = once.WithGloasForkOverride(400_000, null);

        Assert.That(twice.Forks, Has.Length.EqualTo(BeaconChainSpec.Sepolia.Forks.Length));
        Assert.That(twice.ForkAtEpoch(BeaconChainSpec.Sepolia.FuluForkEpoch), Is.EqualTo(BeaconFork.Fulu));
    }

    [Test]
    public void Override_keeps_every_property_it_does_not_change()
    {
        BeaconChainSpec spec = BeaconChainSpec.Sepolia.WithGloasForkOverride(400_000, "0x90000099");

        string[] changed = [nameof(BeaconChainSpec.Forks), nameof(BeaconChainSpec.GloasForkEpoch), nameof(BeaconChainSpec.GloasForkVersion)];
        foreach (PropertyInfo property in typeof(BeaconChainSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (changed.Contains(property.Name)) continue;
            Assert.That(property.GetValue(spec), Is.EqualTo(property.GetValue(BeaconChainSpec.Sepolia)), property.Name);
        }
    }

    [Test]
    public void Unscheduling_a_network_without_a_gloas_epoch_changes_nothing() =>
        Assert.That(BeaconChainSpec.Hoodi.WithGloasForkOverride(Presets.FarFutureEpoch, null), Is.SameAs(BeaconChainSpec.Hoodi));

    [Test]
    public void No_override_returns_the_built_in_spec() =>
        Assert.That(BeaconChainSpec.Sepolia.WithGloasForkOverride(null, null), Is.SameAs(BeaconChainSpec.Sepolia));

    [TestCase(271_999ul, "0x90000099", "below the Fulu fork epoch")]
    [TestCase(500_000ul, "0x9000", "not 4 bytes of hex")]
    [TestCase(500_000ul, "0x900000991", "not 4 bytes of hex")]
    [TestCase(500_000ul, "0x9000009g", "not 4 bytes of hex")]
    [TestCase(500_000ul, "", "not 4 bytes of hex")]
    [TestCase(500_000ul, "0x90000075", "repeats an earlier fork version")]
    public void Invalid_override_fails_with_a_message_naming_the_key(ulong epoch, string version, string expected)
    {
        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(
            () => BeaconChainSpec.Sepolia.WithGloasForkOverride(epoch, version))!;

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(ex.Message, Does.Contain("BeaconChain.GloasFork"));
        Assert.That(ex.Message, Does.Contain(expected));
    }

    [Test]
    public void Epoch_without_a_version_needs_one_where_the_network_has_no_built_in_version()
    {
        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(
            () => BeaconChainSpec.Hoodi.WithGloasForkOverride(OverrideEpoch, null))!;

        Assert.That(ex.Message, Does.Contain("needs BeaconChain.GloasForkVersion"));
    }

    [Test]
    public void Version_without_an_epoch_fails_where_the_network_has_no_gloas_epoch()
    {
        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(
            () => BeaconChainSpec.Hoodi.WithGloasForkOverride(null, "0x80000910"))!;

        Assert.That(ex.Message, Does.Contain("has no effect"));
    }

    [Test]
    public void Config_keys_reach_the_spec_the_driver_runs_on()
    {
        using IContainer container = BeaconChainTestContainer.Builder(
            BlockchainIds.Hoodi,
            config: new BeaconChainConfig { GloasForkEpoch = OverrideEpoch, GloasForkVersion = "0x80000910" }).Build();

        BeaconChainSpec spec = container.Resolve<BeaconChainSpec>();

        using System.IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(spec.ChainId, Is.EqualTo(BlockchainIds.Hoodi));
        Assert.That(spec.ForkAtEpoch(OverrideEpoch), Is.EqualTo(BeaconFork.Gloas));
        Assert.That(spec.GloasForkVersion, Is.EqualTo(Bytes.FromHexString("0x80000910")));
    }

    [Test]
    public void Invalid_config_stops_start_up_with_the_configuration_error()
    {
        using IContainer container = BeaconChainTestContainer.Builder(
            config: new BeaconChainConfig { GloasForkEpoch = 1, GloasForkVersion = "0x08000000" }).Build();

        DependencyResolutionException wrapped = Assert.Throws<DependencyResolutionException>(() => container.Resolve<BeaconChainSpec>())!;

        Assert.That(wrapped.GetBaseException(), Is.TypeOf<InvalidConfigurationException>().With.Message.Contains("below the Fulu fork epoch"));
    }
}
