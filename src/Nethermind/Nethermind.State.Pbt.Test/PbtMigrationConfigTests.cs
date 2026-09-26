// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.State.Pbt.Migration;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Json;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtMigrationConfigTests
{
    private static ChainSpec Chain() => new()
    {
        Genesis = Build.A.Block.WithTimestamp(10).TestObject,
        Parameters = new ChainParameters { Eip8347TransitionTimestamp = 100, Eip7928TransitionTimestamp = 10, Eip6780TransitionTimestamp = 10 }
    };

    private static PbtConfig Config() => new() { Enabled = true, MigrationGenesisBootstrap = true };
    private static FlatDbConfig Flat() => new() { Enabled = true, Layout = FlatLayout.Flat };

    [Test]
    public void Independent_genesis_preserves_commitment_boundary([Values(47UL, 48UL, 49UL)] ulong timestamp)
    {
        using Stream genesis = typeof(PbtMigrationConfigTests).Assembly.GetManifestResourceStream("Nethermind.State.Pbt.Test.Fixtures.Eip8347.genesis.json")!;
        ChainSpec chain = new GethGenesisLoader(new EthereumJsonSerializer()).Load(genesis);
        ChainSpecBasedSpecProvider specs = new(chain);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Parameters.Eip8347TransitionTimestamp, Is.EqualTo(48));
            Assert.That(specs.GetSpec(1, timestamp).IsEip8347Enabled, Is.EqualTo(timestamp >= 48));
        }
        Assert.DoesNotThrow(() => PbtMigrationConfigValidator.Validate(Config(), Flat(), chain, "target"));
    }

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
            Assert.That(config.MigrationPreimageSourcePath, Is.Null);
        }
        Assert.DoesNotThrow(() => PbtMigrationConfigValidator.Validate(config, new FlatDbConfig(), new ChainSpec(), "target"));
    }

    [Test]
    public void Accepts_distinct_sources([Values("genesis", "portable", "preimage", "none")] string source)
    {
        PbtConfig config = Config();
        if (source != "genesis")
        {
            config.MigrationGenesisBootstrap = false;
            config.MigrationAnchor = 25;
            if (source == "portable")
            {
                config.MigrationSnapshotPath = "source/snapshot.pbt";
                config.MigrationPreimagesPath = "source/preimages.bin";
            }
            else if (source == "preimage") config.MigrationPreimageSourcePath = "source/db";
        }
        Assert.DoesNotThrow(() => PbtMigrationConfigValidator.Validate(config, Flat(), Chain(), "target"));
    }

    [Test]
    public void Rejects_invalid_configuration([Values(
        "mirror", "fake", "import", "scan", "flat-disabled", "layout",
        "no-bal", "late-bal", "genesis-bal", "genesis-deletion", "no-deletion", "late-deletion", "partial", "mixed", "no-anchor", "negative-anchor", "overlap", "whitespace")] string invalid)
    {
        PbtConfig config = Config();
        FlatDbConfig flat = Flat();
        ChainSpec chain = Chain();
        switch (invalid)
        {
            case "mirror": config.MirrorFlat = true; break;
            case "fake": config.FakeMatchingStateRoot = true; break;
            case "import": config.ImportFromPreimageFlat = true; break;
            case "scan": config.ScanTree = true; break;
            case "flat-disabled": flat.Enabled = false; break;
            case "layout": flat.Layout = FlatLayout.PreimageFlat; break;
            case "no-bal": chain.Parameters.Eip7928TransitionTimestamp = null; break;
            case "late-bal": chain.Parameters.Eip7928TransitionTimestamp = 100; break;
            case "genesis-bal": chain.Parameters.Eip7928TransitionTimestamp = 11; break;
            case "genesis-deletion": chain.Parameters.Eip6780TransitionTimestamp = 11; break;
            case "no-deletion": chain.Parameters.Eip6780TransitionTimestamp = null; break;
            case "late-deletion": chain.Parameters.Eip6780TransitionTimestamp = 100; break;
            case "partial": config.MigrationSnapshotPath = "source/snapshot.pbt"; break;
            case "mixed": config.MigrationPreimageSourcePath = "source/db"; break;
            case "no-anchor": config.MigrationGenesisBootstrap = false; config.MigrationPreimageSourcePath = "source/db"; break;
            case "negative-anchor": config.MigrationAnchor = -1; break;
            case "overlap": config.MigrationPreimageSourcePath = "target/source"; config.MigrationAnchor = 25; config.MigrationGenesisBootstrap = false; break;
            case "whitespace": config.MigrationPreimageSourcePath = " "; config.MigrationAnchor = 25; config.MigrationGenesisBootstrap = false; break;
        }
        Assert.Throws<InvalidConfigurationException>(() => PbtMigrationConfigValidator.Validate(config, flat, chain, "target"));
    }

    [Test]
    public void Scheduled_binary_trie_requires_explicit_runtime_enablement([Values] bool requested, [Values(10UL, 100UL)] ulong activation)
    {
        ChainSpec chain = Chain();
        chain.Parameters.Eip8347TransitionTimestamp = activation;
        PbtPlugin plugin = new(requested ? Config() : new PbtConfig(), Flat(), chain, new InitConfig { BaseDbPath = "target" });
        Assert.That(plugin.Enabled, Is.True);
        if (!requested) Assert.Throws<InvalidConfigurationException>(() => _ = plugin.Module);
        else if (activation > chain.Genesis!.Timestamp) Assert.That(plugin.Module, Is.TypeOf<PbtMigrationModule>());
        else Assert.That(plugin.Module, Is.TypeOf<PbtModule>());
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

    /// <remarks>Exporting reads the flat state rather than replacing it, so the plugin must not need
    /// Pbt.Enabled and must not pull in a PBT backend module.</remarks>
    [Test]
    public void Export_selects_its_own_module_without_enabling_the_binary_tree()
    {
        ChainSpec chain = Chain();
        chain.Parameters.Eip8347TransitionTimestamp = null;
        PbtConfig config = new() { MigrationExportPath = Path.Combine(Path.GetTempPath(), "pbt-export-" + System.Guid.NewGuid().ToString("N")) };
        PbtPlugin plugin = new(config, new FlatDbConfig { Enabled = true, Layout = FlatLayout.PreimageFlat }, chain, new InitConfig { BaseDbPath = "target" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plugin.Enabled, Is.True);
            Assert.That(plugin.Module, Is.TypeOf<PbtExportModule>());
        }
    }

    [Test]
    public void Standalone_from_genesis_runs_alongside_flat([Values(null, 0UL, 10UL)] ulong? activation)
    {
        ChainSpec chain = Chain();
        chain.Parameters.Eip8347TransitionTimestamp = activation;
        PbtPlugin plugin = new(new PbtConfig { Enabled = true }, Flat(), chain, new InitConfig());
        Assert.That(plugin.Module, Is.TypeOf<PbtModule>());
    }
}
