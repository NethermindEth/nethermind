// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

public class CheckpointSyncTests
{
    [Test]
    [Explicit("Hits live checkpoint provider")]
    public async Task Live_checkpoint_sync_verifies_anchor_and_resumes_from_store_without_http()
    {
        TestLogManager logManager = new(LogLevel.Info);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        // Not the config default (sigp.io): at the time of writing its TLS certificate was expired.
        BeaconChainConfig config = new() { CheckpointSyncUrl = "https://beaconstate.ethstaker.cc" };

        Stopwatch stopwatch = Stopwatch.StartNew();
        CheckpointAnchor anchor;
        using (CheckpointSync sync = new(config, BeaconChainSpec.Mainnet, store, logManager))
        {
            anchor = await sync.RunAsync(CancellationToken.None);
        }
        TestContext.Out.WriteLine($"Checkpoint sync took {stopwatch.Elapsed.TotalSeconds:F1} s");

        Assert.Multiple(() =>
        {
            Assert.That(anchor.State.Fork!.CurrentVersion, Is.EqualTo(Bytes.FromHexString("0x06000000")), "finalized mainnet state must be Fulu");
            Assert.That(anchor.State.Validators!.Length, Is.GreaterThan(1_000_000));
            Assert.That(anchor.Block, Is.Not.Null);
            Assert.That(anchor.Block!.Message!.StateRoot, Is.EqualTo(anchor.StateRoot));
            Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out ulong anchorSlot), Is.True);
            Assert.That(anchorRoot, Is.EqualTo(anchor.BlockRoot));
            Assert.That(anchorSlot, Is.EqualTo(anchor.Block.Message.Slot));
            Assert.That(store.TryGetState(anchor.BlockRoot, out byte[]? persistedState), Is.True);
            Assert.That(persistedState!.Length, Is.GreaterThan(100 * 1024 * 1024));
            Assert.That(store.TryGetBlock(anchor.BlockRoot, out _), Is.True);
        });

        // The unroutable URL proves the second start resumes from the persisted anchor without HTTP:
        // had it attempted checkpoint sync it would have failed and never populated the pubkey cache.
        BeaconChainConfig offlineConfig = new() { CheckpointSyncUrl = "http://invalid.localhost:1" };
        PubkeyCache pubkeyCache = new();
        stopwatch.Restart();
        using (CheckpointSync offlineSync = new(offlineConfig, BeaconChainSpec.Mainnet, store, logManager))
        using (BeaconChainService service = new(offlineConfig, store, pubkeyCache, offlineSync, CreateDetector(logManager), logManager))
        {
            await service.Start();
        }
        TestContext.Out.WriteLine($"Resume + pubkey cache build took {stopwatch.Elapsed.TotalSeconds:F1} s");

        Assert.That(pubkeyCache.Count, Is.EqualTo(anchor.State.Validators!.Length));

        // Third start loads the persisted pubkey cache instead of rebuilding it.
        PubkeyCache reloadedCache = new();
        using (CheckpointSync offlineSync = new(offlineConfig, BeaconChainSpec.Mainnet, store, logManager))
        using (BeaconChainService service = new(offlineConfig, store, reloadedCache, offlineSync, CreateDetector(logManager), logManager))
        {
            await service.Start();
        }

        Assert.That(reloadedCache.Count, Is.EqualTo(anchor.State.Validators!.Length));
    }

    private static ExternalClDetector CreateDetector(ILogManager logManager) =>
        new(new BeaconChainConfig(), new Lazy<IEngineRpcModule>(Substitute.For<IEngineRpcModule>()), logManager);
}
