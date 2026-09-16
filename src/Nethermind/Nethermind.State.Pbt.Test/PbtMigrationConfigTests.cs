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
            Assert.That(config.MigrationManifestPath, Is.Null);
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
            if (source == "portable")
            {
                config.MigrationManifestPath = "source/manifest.json";
                config.MigrationSnapshotPath = "source/snapshot.pbt";
                config.MigrationPreimagesPath = "source/preimages.bin";
            }
            else if (source == "preimage")
            {
                config.MigrationManifestPath = "source/manifest.json";
                config.MigrationPreimageSourcePath = "source/db";
            }
        }
        Assert.DoesNotThrow(() => PbtMigrationConfigValidator.Validate(config, Flat(), Chain(), "target"));
    }

    [Test]
    public void Rejects_invalid_configuration([Values(
        "mirror", "fake", "import", "scan", "flat-disabled", "layout",
        "no-bal", "late-bal", "genesis-bal", "genesis-deletion", "no-deletion", "late-deletion", "partial", "mixed", "no-manifest", "overlap", "whitespace")] string invalid)
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
            case "no-manifest": config.MigrationGenesisBootstrap = false; config.MigrationPreimageSourcePath = "source/db"; break;
            case "overlap": config.MigrationManifestPath = "target/manifest.json"; break;
            case "whitespace": config.MigrationManifestPath = " "; break;
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

    [Test]
    public void Offline_export_requires_isolated_new_output_and_source_mode([Values("valid", "unscheduled", "portable", "target", "empty")] string mode)
    {
        PbtConfig config = Config();
        ChainSpec chain = Chain();
        config.MigrationExportPath = Path.Combine(Path.GetTempPath(), "pbt-export-" + System.Guid.NewGuid().ToString("N"));
        if (mode == "unscheduled") chain.Parameters.Eip8347TransitionTimestamp = null;
        if (mode == "portable")
        {
            config.MigrationGenesisBootstrap = false;
            config.MigrationManifestPath = "source/manifest.json";
            config.MigrationSnapshotPath = "source/snapshot.pbt";
            config.MigrationPreimagesPath = "source/preimages.bin";
        }
        if (mode == "target") config.MigrationExportPath = "target/export";
        if (mode == "empty") config.MigrationExportPath = " ";
        if (mode == "valid") Assert.DoesNotThrow(() => PbtMigrationConfigValidator.Validate(config, Flat(), chain, "target"));
        else Assert.Throws<InvalidConfigurationException>(() => PbtMigrationConfigValidator.Validate(config, Flat(), chain, "target"));
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
