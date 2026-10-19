// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Init;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatArchiveModeTests
{
    [Test]
    public void A_snap_synced_archive_keeps_history_and_proofs_from_its_pivot()
    {
        FlatDbConfig flat = new() { Enabled = true };

        Apply(flat, PruningMode.None, snapSync: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flat.HistoryEnabled, Is.True);
            Assert.That(flat.ArchiveProofBuildEnabled, Is.True);
            Assert.That(flat.ArchiveProofServeEnabled, Is.True);
        }
    }

    [Test]
    public void An_archive_synced_from_genesis_keeps_history_but_its_own_proof_settings()
    {
        FlatDbConfig flat = new() { Enabled = true };

        Apply(flat, PruningMode.None, snapSync: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flat.HistoryEnabled, Is.True);
            Assert.That(flat.ArchiveProofBuildEnabled, Is.False, "building proofs over the whole chain is the operator's choice");
            Assert.That(flat.ArchiveProofServeEnabled, Is.False);
        }
    }

    [Test]
    public void An_explicit_setting_always_stands()
    {
        FlatDbConfig flat = new() { Enabled = true };
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetRawValue("FlatDb", nameof(IFlatDbConfig.ArchiveProofBuildEnabled)).Returns("false");

        Apply(flat, PruningMode.None, snapSync: true, configProvider);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flat.HistoryEnabled, Is.True);
            Assert.That(flat.ArchiveProofBuildEnabled, Is.False);
            Assert.That(flat.ArchiveProofServeEnabled, Is.True);
        }
    }

    public enum Source { Arguments, Json, Environment }

    // Each source names the category its own way, so the real provider is the only proof that a setting is seen.
    [Test]
    public void An_explicit_setting_from_any_config_source_stands([Values] Source source)
    {
        ConfigProvider configProvider = new();
        string? jsonPath = null;
        switch (source)
        {
            case Source.Arguments:
                configProvider.AddSource(new ArgsConfigSource(new Dictionary<string, string> { ["FlatDb.ArchiveProofBuildEnabled"] = "false" }));
                break;
            case Source.Json:
                jsonPath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{nameof(FlatArchiveModeTests)}-{Guid.NewGuid():N}.json");
                File.WriteAllText(jsonPath, """{ "FlatDb": { "ArchiveProofBuildEnabled": false } }""");
                configProvider.AddSource(new JsonConfigSource(jsonPath));
                break;
            case Source.Environment:
                IEnvironment environment = Substitute.For<IEnvironment>();
                environment.GetEnvironmentVariable(Arg.Any<string>()).Returns((string?)null);
                environment.GetEnvironmentVariable("NETHERMIND_FLATDBCONFIG_ARCHIVEPROOFBUILDENABLED").Returns("false");
                environment.GetEnvironmentVariables().Returns(new Hashtable { ["NETHERMIND_FLATDBCONFIG_ARCHIVEPROOFBUILDENABLED"] = "false" });
                configProvider.AddSource(new EnvConfigSource(environment));
                break;
        }

        try
        {
            configProvider.Initialize();
            IFlatDbConfig flat = configProvider.GetConfig<IFlatDbConfig>();
            flat.Enabled = true;

            FlatArchiveMode.Apply(
                flat,
                new PruningConfig { Mode = PruningMode.None },
                new SyncConfig { FastSync = true, SnapSync = true },
                configProvider,
                LimboLogs.Instance.GetClassLogger<FlatArchiveModeTests>());

            using (Assert.EnterMultipleScope())
            {
                Assert.That(flat.ArchiveProofBuildEnabled, Is.False);
                Assert.That(flat.ArchiveProofServeEnabled, Is.True, "a setting left unset still follows archive mode");
            }
        }
        finally
        {
            if (jsonPath is not null) File.Delete(jsonPath);
        }
    }

    [Test]
    public void A_blank_setting_does_not_count_as_a_choice()
    {
        FlatDbConfig flat = new() { Enabled = true };
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetRawValue("FlatDbConfig", nameof(IFlatDbConfig.ArchiveProofBuildEnabled)).Returns(" ");

        Apply(flat, PruningMode.None, snapSync: true, configProvider);

        Assert.That(flat.ArchiveProofBuildEnabled, Is.True);
    }

    [Test]
    public void Fast_sync_without_snap_is_not_a_snap_synced_archive()
    {
        FlatDbConfig flat = new() { Enabled = true };

        FlatArchiveMode.Apply(
            flat,
            new PruningConfig { Mode = PruningMode.None },
            new SyncConfig { FastSync = true, SnapSync = false },
            Substitute.For<IConfigProvider>(),
            LimboLogs.Instance.GetClassLogger<FlatArchiveModeTests>());

        Assert.That(flat.ArchiveProofBuildEnabled, Is.False);
    }

    [Test]
    public void History_disabled_explicitly_leaves_the_node_a_full_node()
    {
        FlatDbConfig flat = new() { Enabled = true };
        IConfigProvider configProvider = Substitute.For<IConfigProvider>();
        configProvider.GetRawValue("FlatDb", nameof(IFlatDbConfig.HistoryEnabled)).Returns("false");

        Apply(flat, PruningMode.None, snapSync: true, configProvider);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flat.HistoryEnabled, Is.False);
            Assert.That(flat.ArchiveProofBuildEnabled, Is.False);
        }
    }

    [Test]
    public void A_bounded_history_gets_no_proofs()
    {
        FlatDbConfig flat = new() { Enabled = true, HistoryRetention = HistoryRetentionMode.Rolling };

        Apply(flat, PruningMode.None, snapSync: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flat.HistoryEnabled, Is.True);
            Assert.That(flat.ArchiveProofBuildEnabled, Is.False, "windowed history cannot serve proofs");
        }
    }

    [TestCase(true, PruningMode.Hybrid)]
    [TestCase(false, PruningMode.None)]
    public void Nothing_changes_without_archive_mode_on_the_flat_backend(bool flatEnabled, PruningMode mode)
    {
        FlatDbConfig flat = new() { Enabled = flatEnabled };

        Apply(flat, mode, snapSync: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(flat.HistoryEnabled, Is.False);
            Assert.That(flat.ArchiveProofBuildEnabled, Is.False);
        }
    }

    private static void Apply(FlatDbConfig flat, PruningMode mode, bool snapSync, IConfigProvider? configProvider = null) =>
        FlatArchiveMode.Apply(
            flat,
            new PruningConfig { Mode = mode },
            new SyncConfig { FastSync = snapSync, SnapSync = snapSync },
            configProvider ?? Substitute.For<IConfigProvider>(),
            LimboLogs.Instance.GetClassLogger<FlatArchiveModeTests>());
}
