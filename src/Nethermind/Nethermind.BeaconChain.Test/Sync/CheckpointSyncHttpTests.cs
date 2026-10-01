// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.ForkChoice;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// Every public provider serves the finalized checkpoint over the beacon API, labelled with its fork in the
/// Eth-Consensus-Version header; refusing the Gloas label refuses every checkpoint once Gloas finalizes.
/// </summary>
[HardTimeout(60_000)]
public class CheckpointSyncHttpTests
{
    public enum AdvancedCheckpoint
    {
        WithinFulu,
        AcrossTheGloasUpgrade,
        PastAnEmptyGloasEpochStart,
    }

    /// <summary>An epoch-start state advanced beyond its block holds a filled latest header root (consensus-specs beacon-chain.md process_slot).
    /// The anchor must use that block's verified post-state.</summary>
    [Test]
    public async Task An_advanced_checkpoint_uses_its_blocks_post_state(
        [Values] AdvancedCheckpoint advancedCheckpoint, [Values] bool fromFile, [Values] bool invalidPostState, [Values(null, false, true)] bool? conflictingCheckpoint)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        ForkedSignedBeaconBlock block;
        byte[] postState;
        byte[] advancedState;
        Hash256 stateRoot;
        if (advancedCheckpoint == AdvancedCheckpoint.PastAnEmptyGloasEpochStart)
        {
            BeaconStateGloas advanced = chain.First.PostState.Clone();
            GloasSlotProcessing.ProcessSlots(advanced, 2 * Presets.SlotsPerEpoch, new EpochCache());
            block = new ForkedSignedBeaconBlock.OfGloas(chain.First.Block);
            postState = BeaconStateGloas.Encode(chain.First.PostState);
            advancedState = BeaconStateGloas.Encode(advanced);
            stateRoot = chain.First.Block.Message!.StateRoot!;
        }
        else
        {
            BeaconStateFulu advanced = chain.AnchorState.Clone();
            SlotProcessing.ProcessSlots(advanced, advancedCheckpoint == AdvancedCheckpoint.AcrossTheGloasUpgrade ? GloasTestFixtures.BoundarySlot : 1, new EpochCache());
            advancedState = advancedCheckpoint == AdvancedCheckpoint.AcrossTheGloasUpgrade
                ? BeaconStateGloas.Encode(GloasForkTransition.UpgradeToGloas(advanced, GloasCheckpointFiles.Spec))
                : BeaconStateFulu.Encode(advanced);
            block = new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain.AnchorBlock, Signature = new BlsSignature(new byte[BlsSignature.Length]) });
            postState = BeaconStateFulu.Encode(chain.AnchorState);
            stateRoot = chain.AnchorBlock.StateRoot!;
        }

        Hash256 root = block.ComputeMessageRoot();
        await using WebApplication provider = await StartProviderAsync(null, advancedState, block, stateRoot, invalidPostState ? advancedState : postState);
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(advancedState, block);
        await using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig
        {
            CheckpointSyncUrl = provider.Urls.First(),
            CheckpointStateFile = fromFile ? files.StateFile : null,
            WeakSubjectivityCheckpoint = conflictingCheckpoint is null ? null : $"{(conflictingCheckpoint.Value ? Hash256.Zero : root)}:{(advancedCheckpoint == AdvancedCheckpoint.WithinFulu ? 0 : 1)}",
        }).AddSingleton(GloasCheckpointFiles.Spec).Build();
        BeaconChainStore store = container.Resolve<BeaconChainStore>();
        CheckpointSync sync = container.Resolve<CheckpointSync>();
        if (invalidPostState || conflictingCheckpoint == true)
        {
            Assert.ThrowsAsync<System.IO.InvalidDataException>(() => sync.RunAsync(CancellationToken.None));
            Assert.That(store.TryGetAnchor(out _, out _), Is.False);
            return;
        }

        CheckpointAnchor anchor = await sync.RunAsync(CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.BlockRoot, Is.EqualTo(root));
            Assert.That(anchor.State.Slot, Is.EqualTo(block.Slot));
            Assert.That(anchor.State.Fork, Is.EqualTo(advancedCheckpoint == AdvancedCheckpoint.PastAnEmptyGloasEpochStart ? BeaconFork.Gloas : BeaconFork.Fulu));
            Assert.That(anchor.StateRoot, Is.EqualTo(stateRoot));
            Assert.That(store.TryGetState(root, out byte[]? stored), Is.True);
            Assert.That(stored, Is.EqualTo(postState));
            Assert.That(store.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint), conflictingCheckpoint is null ? Is.Null : Is.Not.Null);
        }
        sync.ThrowIfResumedAnchorMissesWeakSubjectivityCheckpoint(anchor.State, anchor.BlockRoot);
    }

    [Test]
    public async Task An_advanced_checkpoint_bounds_its_post_state_body()
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        BeaconStateFulu advanced = chain.AnchorState.Clone();
        SlotProcessing.ProcessSlots(advanced, 1, new EpochCache());
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain.AnchorBlock, Signature = new BlsSignature(new byte[BlsSignature.Length]) });
        byte[] stateSsz = BeaconStateFulu.Encode(advanced);
        await using WebApplication provider = await StartProviderAsync(null, stateSsz, block, chain.AnchorBlock.StateRoot, new byte[stateSsz.Length + 1]);
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(stateSsz, block);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = files.StateFile, CheckpointSyncUrl = provider.Urls.First() }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance)
        {
            MaxBodyBytes = stateSsz.Length,
        };

        System.IO.InvalidDataException refusal = Assert.ThrowsAsync<System.IO.InvalidDataException>(() => sync.RunAsync(CancellationToken.None))!;
        Assert.That(refusal.Message, Does.Contain("byte limit"));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    [TestCase("gloas")]
    [TestCase("Gloas")]
    [TestCase(null, Description = "Without the header the state's slot selects its layout.")]
    public async Task A_gloas_checkpoint_served_over_the_beacon_api_is_anchored_in_the_gloas_shape(string? consensusVersion)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        await using WebApplication provider = await StartProviderAsync(consensusVersion);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointSyncUrl = provider.Urls.First() }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        CheckpointAnchor anchor = await sync.RunAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.State, Is.TypeOf<ForkedBeaconState.OfGloas>());
            Assert.That(anchor.Block, Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>(), "the block endpoint answered for the derived anchor root");
            Assert.That(anchor.BlockRoot, Is.EqualTo(first.Root));
            Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out _), Is.True);
            Assert.That(anchorRoot, Is.EqualTo(first.Root));
        }
    }

    [Test]
    public async Task A_checkpoint_labelled_with_an_unknown_fork_is_refused_before_anything_is_persisted()
    {
        await using WebApplication provider = await StartProviderAsync("heze");
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointSyncUrl = provider.Urls.First() }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        NotSupportedException ex = Assert.ThrowsAsync<NotSupportedException>(() => sync.RunAsync(CancellationToken.None))!;

        Assert.That(ex.Message, Does.Contain("'heze'"));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    /// <summary>A beacon API serving <see cref="ForkCrossingChain.First"/> as the finalized state and its block by root.</summary>
    private static async Task<WebApplication> StartProviderAsync(string? consensusVersion, byte[]? finalizedState = null,
        ForkedSignedBeaconBlock? block = null, Hash256? postStateRoot = null, byte[]? postState = null)
    {
        ForkCrossingChain.ChainBlock first = ForkCrossingChain.Instance.First;
        byte[] stateSsz = finalizedState ?? BeaconStateGloas.Encode(first.PostState);
        block ??= new ForkedSignedBeaconBlock.OfGloas(first.Block);
        Hash256 blockRoot = block.ComputeMessageRoot();
        byte[] blockSsz = SignedBeaconBlockCodec.Encode(block, GloasCheckpointFiles.Spec);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        app.MapGet("/eth/v2/debug/beacon/states/finalized", async (HttpContext c) =>
        {
            if (consensusVersion is not null)
            {
                c.Response.Headers["Eth-Consensus-Version"] = consensusVersion;
            }

            c.Response.ContentType = "application/octet-stream";
            await c.Response.Body.WriteAsync(stateSsz);
        });
        app.MapGet("/eth/v2/beacon/blocks/{root}", async (HttpContext c, string root) =>
        {
            if (root != blockRoot.ToString())
            {
                c.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            c.Response.ContentType = "application/octet-stream";
            await c.Response.Body.WriteAsync(blockSsz);
        });
        app.MapGet("/eth/v2/debug/beacon/states/{root}", async (HttpContext c, string root) =>
        {
            if (root != postStateRoot?.ToString() || postState is null)
            {
                c.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            c.Response.ContentType = System.Net.Mime.MediaTypeNames.Application.Octet;
            await c.Response.Body.WriteAsync(postState);
        });
        await app.StartAsync();
        return app;
    }
}
