// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.State.Pbt.Migration;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtMigrationConfigTests
{
    [Test]
    public void Snapshot_coordination_defaults_match_flat()
    {
        PbtConfig pbt = new();
        FlatDbConfig flat = new();
        string[] properties = ["EnableLongFinality", "LongFinalityMaxReorgDepth", "MaxInMemoryBaseSnapshotCount",
            "MaxInMemorySnapshotBytes", "MaxInFlightCompactJob", "InlineCompaction", "RegenerateCompactionOffset",
            "ArenaFileSizeBytes", "PersistedSnapshotDedicatedArenaThresholdBytes", "PersistedSnapshotArenaPageCacheBytes",
            "PersistedSnapshotPunchHoleOnReclaim", "PersistedSnapshotMaxCompactSize", "ValidatePersistedSnapshot",
            "PersistedSnapshotBloomBitsPerKey", "InMemorySnapshotBloomBitsPerKey"];
        using (Assert.EnterMultipleScope())
            foreach (string name in properties)
                Assert.That(typeof(PbtConfig).GetProperty(name)!.GetValue(pbt),
                    Is.EqualTo(typeof(FlatDbConfig).GetProperty(name)!.GetValue(flat)), name);
    }

    [Test]
    public void Pbt_schedule_preserves_explicit_values_and_shared_metadata_offset()
    {
        using MemDb metadata = new();
        PbtConfig pbt = new() { CompactSize = 8, CompactionOffset = 3, PersistedSnapshotMaxCompactSize = 64 };
        ICompactionSchedule first = PbtCoreRegistration.CreateCompactionSchedule(metadata, pbt, LimboLogs.Instance);
        ((IDb)metadata).Set(MetadataDbKeys.FlatDbCompactionOffset, Nethermind.Serialization.Rlp.Rlp.Encode(3L).Bytes);
        ICompactionSchedule restored = PbtCoreRegistration.CreateCompactionSchedule(metadata, new PbtConfig
        {
            CompactSize = 8,
            PersistedSnapshotMaxCompactSize = 64,
        }, LimboLogs.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.IsCompactSizeBoundary(5), Is.True);
            Assert.That(restored.IsCompactSizeBoundary(5), Is.True);
            Assert.That(pbt.CompactionOffset, Is.EqualTo(3));
            Assert.That(pbt.CompactSize, Is.EqualTo(8));
        }
    }

    [Test]
    public void Pbt_schedule_regeneration_updates_the_shared_metadata_key()
    {
        using MemDb metadata = new();
        ((IDb)metadata).Set(MetadataDbKeys.FlatDbCompactionOffset, Nethermind.Serialization.Rlp.Rlp.Encode(long.MaxValue).Bytes);
        PbtCoreRegistration.CreateCompactionSchedule(metadata, new PbtConfig { RegenerateCompactionOffset = true }, LimboLogs.Instance);
        long stored = new Nethermind.Serialization.Rlp.RlpReader(((IDb)metadata).Get(MetadataDbKeys.FlatDbCompactionOffset)!).DecodeLong();
        Assert.That(stored, Is.InRange(0L, (long)int.MaxValue - 1));
    }

    private static ChainSpec Chain() => new()
    {
        Genesis = Build.A.Block.WithTimestamp(10).TestObject,
        Parameters = new ChainParameters { Eip8347TransitionTimestamp = 100, Eip7928TransitionTimestamp = 10, Eip6780TransitionTimestamp = 10 }
    };

    private static PbtConfig Config() => new() { Enabled = true, MigrationGenesisBootstrap = true };
    private static FlatDbConfig Flat() => new() { Enabled = true, Layout = FlatLayout.Flat };

    [Test]
    public void Defaults_leave_migration_disabled()
    {
        PbtConfig config = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.MigrationGenesisBootstrap, Is.False);
            Assert.That(config.MigrationAnchor, Is.Null);
            Assert.That(config.MigrationSnapshotPath, Is.Null);
            Assert.That(config.MigrationPreimagesPath, Is.Null);
        }
        Assert.DoesNotThrow(() => PbtMigrationConfigValidator.Validate(config, new FlatDbConfig(), new ChainSpec(), "target"));
    }

    [Test]
    public void Accepts_distinct_sources([Values("genesis", "snapshot", "snapshot+preimages", "none")] string source)
    {
        PbtConfig config = Config();
        if (source != "genesis")
        {
            config.MigrationGenesisBootstrap = false;
            config.MigrationAnchor = 25;
            if (source is "snapshot" or "snapshot+preimages") config.MigrationSnapshotPath = "source/snapshot.pbt";
            if (source is "snapshot+preimages") config.MigrationPreimagesPath = "source/preimages.bin";
        }
        FlatDbConfig flat = new() { Enabled = true, Layout = FlatLayout.PreimageFlat };
        Assert.DoesNotThrow(() => PbtMigrationConfigValidator.Validate(config, flat, Chain(), "target"));
    }

    [Test]
    public void Rejects_invalid_configuration([Values(
        "fake", "import", "scan", "flat-disabled",
        "no-bal", "late-bal", "genesis-bal", "genesis-deletion", "no-deletion", "late-deletion", "mixed", "preimages-alone", "no-anchor", "negative-anchor", "overlap", "whitespace")] string invalid)
    {
        PbtConfig config = Config();
        FlatDbConfig flat = Flat();
        ChainSpec chain = Chain();
        switch (invalid)
        {
            case "fake": config.FakeMatchingStateRoot = true; break;
            case "import": config.ImportFromPreimageFlat = true; break;
            case "scan": config.ScanTree = true; break;
            case "flat-disabled": flat.Enabled = false; break;
            case "no-bal": chain.Parameters.Eip7928TransitionTimestamp = null; break;
            case "late-bal": chain.Parameters.Eip7928TransitionTimestamp = 100; break;
            case "genesis-bal": chain.Parameters.Eip7928TransitionTimestamp = 11; break;
            case "genesis-deletion": chain.Parameters.Eip6780TransitionTimestamp = 11; break;
            case "no-deletion": chain.Parameters.Eip6780TransitionTimestamp = null; break;
            case "late-deletion": chain.Parameters.Eip6780TransitionTimestamp = 100; break;
            case "mixed": config.MigrationSnapshotPath = "source/snapshot.pbt"; break;
            case "preimages-alone": config.MigrationGenesisBootstrap = false; config.MigrationAnchor = 25; config.MigrationPreimagesPath = "source/preimages.bin"; break;
            case "no-anchor": config.MigrationGenesisBootstrap = false; config.MigrationSnapshotPath = "source/snapshot.pbt"; break;
            case "negative-anchor": config.MigrationAnchor = -1; break;
            case "overlap": config.MigrationSnapshotPath = "target/source"; config.MigrationAnchor = 25; config.MigrationGenesisBootstrap = false; break;
            case "whitespace": config.MigrationSnapshotPath = " "; config.MigrationAnchor = 25; config.MigrationGenesisBootstrap = false; break;
        }
        Assert.Throws<InvalidConfigurationException>(() => PbtMigrationConfigValidator.Validate(config, flat, chain, "target"));
    }

    private static IEnumerable<TestCaseData> PluginModules()
    {
        yield return new TestCaseData(new PbtConfig(), Flat(), 10UL, null).SetName("Unrequested_binary_tree_at_genesis_is_rejected");
        yield return new TestCaseData(new PbtConfig(), Flat(), 100UL, null).SetName("Unrequested_scheduled_migration_is_rejected");
        yield return new TestCaseData(Config(), Flat(), 100UL, typeof(PbtMigrationModule)).SetName("Requested_scheduled_migration_selects_migration");
        yield return new TestCaseData(Config(), Flat(), 10UL, typeof(PbtModule)).SetName("Migration_config_with_activation_at_genesis_runs_standalone");
        yield return new TestCaseData(new PbtConfig { Enabled = true }, Flat(), null, typeof(PbtModule)).SetName("Standalone_without_activation_runs_alongside_flat");
        yield return new TestCaseData(new PbtConfig { Enabled = true }, Flat(), 0UL, typeof(PbtModule)).SetName("Standalone_from_zero_activation_runs_alongside_flat");
        yield return new TestCaseData(new PbtConfig { Enabled = true }, Flat(), 10UL, typeof(PbtModule)).SetName("Standalone_from_genesis_runs_alongside_flat");
        yield return new TestCaseData(
            new PbtConfig { MigrationExportPath = Path.Combine(Path.GetTempPath(), "pbt-export-" + Guid.NewGuid().ToString("N")) },
            new FlatDbConfig { Enabled = true, Layout = FlatLayout.PreimageFlat },
            null,
            typeof(PbtExportModule)).SetName("Export_selects_its_own_module_without_enabling_the_binary_tree");
    }

    /// <remarks>Exporting reads the flat state rather than replacing it, so the plugin must not need
    /// Pbt.Enabled and must not pull in a PBT backend module.</remarks>
    [TestCaseSource(nameof(PluginModules))]
    public void Plugin_selects_its_module(PbtConfig config, FlatDbConfig flat, ulong? activation, Type? expectedModule)
    {
        ChainSpec chain = Chain();
        chain.Parameters.Eip8347TransitionTimestamp = activation;
        PbtPlugin plugin = new(config, flat, chain, new InitConfig { BaseDbPath = "target" });
        Assert.That(plugin.Enabled, Is.True);
        if (expectedModule is null) Assert.Throws<InvalidConfigurationException>(() => _ = plugin.Module);
        else Assert.That(plugin.Module, Is.TypeOf(expectedModule));
    }

    [Test]
    public void Migration_rejects_native_flat_history()
    {
        FlatDbConfig flat = Flat();
        flat.HistoryEnabled = true;
        Assert.Throws<InvalidConfigurationException>(() => PbtMigrationConfigValidator.Validate(Config(), flat, Chain(), "target"));
        Assert.DoesNotThrow(() => PbtMigrationConfigValidator.Validate(Config(), flat, new ChainSpec(), "target"));
    }

    /// <remarks>The export reads the node's own preimage-flat state, so it runs on a chain specification that
    /// schedules nothing and refuses to share the node with the binary tree backend.</remarks>
    [Test]
    public void Export_requires_a_preimage_flat_node_and_an_isolated_new_output(
        [Values("valid", "scheduled", "pbt-enabled", "hashed-layout", "flat-disabled", "target", "empty", "negative-distance")] string mode)
    {
        PbtConfig config = new();
        FlatDbConfig flat = new() { Enabled = true, Layout = FlatLayout.PreimageFlat };
        ChainSpec chain = Chain();
        chain.Parameters.Eip8347TransitionTimestamp = null;
        config.MigrationExportPath = Path.Combine(Path.GetTempPath(), "pbt-export-" + System.Guid.NewGuid().ToString("N"));
        switch (mode)
        {
            case "scheduled": chain.Parameters.Eip8347TransitionTimestamp = 100; break;
            case "pbt-enabled": config.Enabled = true; break;
            case "hashed-layout": flat.Layout = FlatLayout.Flat; break;
            case "flat-disabled": flat.Enabled = false; break;
            case "target": config.MigrationExportPath = "target/export"; break;
            case "empty": config.MigrationExportPath = " "; break;
            case "negative-distance": config.ExportStepDistance = -1; break;
        }
        if (mode == "valid") Assert.DoesNotThrow(() => PbtMigrationConfigValidator.Validate(config, flat, chain, "target"));
        else Assert.Throws<InvalidConfigurationException>(() => PbtMigrationConfigValidator.Validate(config, flat, chain, "target"));
    }
}
