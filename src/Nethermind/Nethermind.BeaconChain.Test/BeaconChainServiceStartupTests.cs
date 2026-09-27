// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test;

public class BeaconChainServiceStartupTests
{
    /// <summary>
    /// A database written by a newer schema cannot be read, and a node that keeps running without its driver leaves the
    /// execution layer unfollowed; the refusal must fail the startup step, not only the background run.
    /// </summary>
    [Test]
    public void The_start_step_fails_on_a_database_written_by_a_newer_schema_version()
    {
        TestErrorLogManager logManager = new();
        using IContainer container = BeaconChainTestContainer.Builder(logManager: logManager).Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        uint newer = BeaconChainStore.CurrentSchemaVersion + 1;
        store.SetSchemaVersion(newer);
        store.SetAnchor(TestItem.KeccakA, 1);
        StartBeaconChain step = new(container.Resolve<BeaconChainService>(), container.Resolve<IEngineDriver>(), logManager);

        Assert.That(() => step.Execute(CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains($"schema version {newer}").And.Message.Contains("delete the beaconChain database"));
        Assert.That(logManager.Errors, Is.Empty, "the background run never started, so it read no anchor");
        Assert.That(store.TryGetSchemaVersion(out uint version), Is.True);
        Assert.That(version, Is.EqualTo(newer), "a refused database is not restamped");
    }

    /// <summary>A stored anchor comes from an earlier checkpoint sync, possibly by an older build, so its sync committee keys are checked again on resume.</summary>
    [Test]
    public async Task A_resumed_anchor_with_an_invalid_sync_committee_key_is_refused(
        [Values] bool gloas, [Values] bool nextCommittee, [Values] InvalidSyncCommitteeKey key)
    {
        (TestErrorLogManager.Error[] errors, int pubkeys) = await ResumeAsync(gloas, nextCommittee, key);

        Assert.That(errors, Has.Length.EqualTo(1));
        Assert.That(errors[0].Exception, Is.TypeOf<InvalidDataException>());
        Assert.That(errors[0].Exception!.Message, Does.Contain(SyncCommitteeKeyAnchors.Refusal(nextCommittee, key)));
        Assert.That(pubkeys, Is.Zero, "refused before the pubkey cache is built");
    }

    /// <summary>A valid resumed anchor passes validation and reaches the Gloas stop, the first check after it that needs no network.</summary>
    [Test]
    public async Task A_resumed_anchor_with_valid_sync_committee_keys_goes_on_to_start([Values] bool gloas)
    {
        (TestErrorLogManager.Error[] errors, _) = await ResumeAsync(gloas, nextCommittee: false, key: null);

        Assert.That(errors, Has.Length.EqualTo(1));
        Assert.That(errors[0].Exception, Is.Null, "a named stop, not a refused anchor");
        Assert.That(errors[0].Text, Does.Contain(nameof(BeaconFork.Gloas)).And.Contain("BeaconChain.Enabled"));
    }

    private static async Task<(TestErrorLogManager.Error[] Errors, int PubkeyCount)> ResumeAsync(bool gloas, bool nextCommittee, InvalidSyncCommitteeKey? key)
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        byte[] stateSsz = SyncCommitteeKeyAnchors.EncodeState(gloas, nextCommittee, key, out Hash256 blockRoot);
        store.PutState(blockRoot, stateSsz);
        // A Gloas anchor block stops the driver before the orchestrator, so no test reaches the network.
        store.PutForkedBlock(blockRoot, new ForkedSignedBeaconBlock.OfGloas(ForkCrossingChain.Instance.First.Block));
        store.SetAnchor(blockRoot, 0);

        using IContainer container = BeaconChainTestContainer.Builder().Build();
        TestErrorLogManager logManager = new();
        PubkeyCache pubkeyCache = new();
        BeaconChainConfig config = new() { CheckpointSyncUrl = "http://invalid.localhost:1" };
        using CheckpointSync checkpointSync = new(config, GloasCheckpointFiles.Spec, store, logManager);
        using BeaconChainService service = new(config, GloasCheckpointFiles.Spec, store, pubkeyCache, checkpointSync,
            container.Resolve<BeaconSyncOrchestrator>(), container.Resolve<ExternalClDetector>(), logManager);
        await service.Start();
        return ([.. logManager.Errors], pubkeyCache.Count);
    }
}
