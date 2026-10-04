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
    /// The anchor must use that block's verified post-state, which a state file supplies in its sibling file and never from the provider.</summary>
    [Test]
    public async Task An_advanced_checkpoint_uses_its_blocks_post_state(
        [Values] AdvancedCheckpoint advancedCheckpoint, [Values] bool fromFile, [Values] bool invalidPostState, [Values(null, false, true)] bool? conflictingCheckpoint)
    {
        (byte[] advancedState, ForkedSignedBeaconBlock block, byte[] postState, Hash256 stateRoot, _) = BuildAdvancedCheckpoint(advancedCheckpoint);
        Hash256 root = block.ComputeMessageRoot();
        byte[] servedPostState = invalidPostState || fromFile ? advancedState : postState;
        await using WebApplication provider = await StartProviderAsync(null, advancedState, block, stateRoot, servedPostState);
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(advancedState, block, invalidPostState ? advancedState : postState);
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

    /// <summary>An advanced state file without its block's post-state file is refused by name, not completed from the provider.</summary>
    [Test]
    public async Task An_advanced_checkpoint_state_file_without_its_post_state_file_is_refused()
    {
        (byte[] advancedState, ForkedSignedBeaconBlock block, byte[] postState, Hash256 stateRoot, _) = BuildAdvancedCheckpoint(AdvancedCheckpoint.AcrossTheGloasUpgrade);
        await using WebApplication provider = await StartProviderAsync(null, advancedState, block, stateRoot, postState);
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(advancedState, block);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointSyncUrl = provider.Urls.First(), CheckpointStateFile = files.StateFile }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);

        System.IO.InvalidDataException refusal = Assert.ThrowsAsync<System.IO.InvalidDataException>(() => sync.RunAsync(CancellationToken.None))!;

        Assert.That(refusal.Message, Does.Contain(files.PostStateFile));
        Assert.That(store.TryGetAnchor(out _, out _), Is.False);
    }

    public enum ConfiguredCheckpoint
    {
        ProvenByTheReceivedState,
        LaterEpoch,
        OtherRoot,
    }

    /// <summary>
    /// The anchor of an advanced checkpoint is its block's post-state from an epoch before the checkpoint's, so after a restart the
    /// checkpoint the received state proved must still be accepted, and one it did not prove must be refused with the failed condition.
    /// </summary>
    [Test]
    public async Task A_checkpoint_the_advanced_state_proved_is_accepted_after_restart(
        [Values(AdvancedCheckpoint.AcrossTheGloasUpgrade, AdvancedCheckpoint.PastAnEmptyGloasEpochStart)] AdvancedCheckpoint advancedCheckpoint,
        [Values] ConfiguredCheckpoint configured)
    {
        (byte[] advancedState, ForkedSignedBeaconBlock block, byte[] postState, Hash256 stateRoot, ulong advancedSlot) = BuildAdvancedCheckpoint(advancedCheckpoint);
        Hash256 root = block.ComputeMessageRoot();
        await using WebApplication provider = await StartProviderAsync(null, advancedState, block, stateRoot, postState);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        CheckpointAnchor anchor;
        using (CheckpointSync sync = new(new BeaconChainConfig { CheckpointSyncUrl = provider.Urls.First() }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance))
        {
            anchor = await sync.RunAsync(CancellationToken.None);
        }

        ulong epoch = GloasCheckpointFiles.Spec.GetEpoch(advancedSlot) + (configured == ConfiguredCheckpoint.LaterEpoch ? 1UL : 0UL);
        string checkpoint = $"{(configured == ConfiguredCheckpoint.OtherRoot ? GloasTestFixtures.Hash(0x5A) : root)}:{epoch}";
        using CheckpointSync restarted = new(new BeaconChainConfig { WeakSubjectivityCheckpoint = checkpoint }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance);
        Assert.That(restarted.ProvesCheckpoint(anchor.State, anchor.BlockRoot, CheckpointSync.ParseWeakSubjectivityCheckpoint(checkpoint)!), Is.False,
            "fixture: the stored post-state alone does not prove the checkpoint");

        if (configured == ConfiguredCheckpoint.ProvenByTheReceivedState)
        {
            restarted.ThrowIfResumedAnchorMissesWeakSubjectivityCheckpoint(anchor.State, anchor.BlockRoot);
            Assert.That(store.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint), Is.Not.Null, "the accepted checkpoint is recorded");
            return;
        }

        System.IO.InvalidDataException refusal = Assert.Throws<System.IO.InvalidDataException>(() => restarted.ThrowIfResumedAnchorMissesWeakSubjectivityCheckpoint(anchor.State, anchor.BlockRoot))!;
        Assert.That(refusal.Message, Does.Contain(configured == ConfiguredCheckpoint.LaterEpoch
            ? $"checkpoint sync anchor has its state at slot {advancedSlot}, before epoch {epoch}"
            : $"checkpoint sync anchor has block {root}, not the checkpoint block"));
        Assert.That(store.GetMetadata(BeaconChainMetadataKeys.WeakSubjectivityCheckpoint), Is.Null);
    }

    [Test]
    public async Task An_advanced_checkpoint_bounds_its_post_state_body([Values] bool fromFile)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        BeaconStateFulu advanced = chain.AnchorState.Clone();
        SlotProcessing.ProcessSlots(advanced, 1, new EpochCache());
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain.AnchorBlock, Signature = new BlsSignature(new byte[BlsSignature.Length]) });
        byte[] stateSsz = BeaconStateFulu.Encode(advanced);
        byte[] oversizedPostState = new byte[stateSsz.Length + 1];
        await using WebApplication provider = await StartProviderAsync(null, stateSsz, block, chain.AnchorBlock.StateRoot, oversizedPostState);
        using GloasCheckpointFiles files = GloasCheckpointFiles.Write(stateSsz, block, oversizedPostState);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        using CheckpointSync sync = new(new BeaconChainConfig { CheckpointStateFile = fromFile ? files.StateFile : null, CheckpointSyncUrl = provider.Urls.First() }, GloasCheckpointFiles.Spec, store, LimboLogs.Instance)
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(anchor.State, Is.TypeOf<ForkedBeaconState.OfGloas>());
        Assert.That(anchor.Block, Is.TypeOf<ForkedSignedBeaconBlock.OfGloas>(), "the block endpoint answered for the derived anchor root");
        Assert.That(anchor.BlockRoot, Is.EqualTo(first.Root));
        Assert.That(store.TryGetAnchor(out Hash256? anchorRoot, out _), Is.True);
        Assert.That(anchorRoot, Is.EqualTo(first.Root));
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

    /// <summary>A checkpoint state advanced past its block, that block, the block's post-state and state root, and the advanced state's slot.</summary>
    private static (byte[] AdvancedState, ForkedSignedBeaconBlock Block, byte[] PostState, Hash256 StateRoot, ulong AdvancedSlot) BuildAdvancedCheckpoint(AdvancedCheckpoint advancedCheckpoint)
    {
        ForkCrossingChain chain = ForkCrossingChain.Instance;
        if (advancedCheckpoint == AdvancedCheckpoint.PastAnEmptyGloasEpochStart)
        {
            BeaconStateGloas advanced = chain.First.PostState.Clone();
            GloasSlotProcessing.ProcessSlots(advanced, 2 * Presets.SlotsPerEpoch, new EpochCache());
            return (BeaconStateGloas.Encode(advanced), new ForkedSignedBeaconBlock.OfGloas(chain.First.Block), BeaconStateGloas.Encode(chain.First.PostState),
                chain.First.Block.Message!.StateRoot!, advanced.Slot);
        }

        BeaconStateFulu advancedFulu = chain.AnchorState.Clone();
        SlotProcessing.ProcessSlots(advancedFulu, advancedCheckpoint == AdvancedCheckpoint.AcrossTheGloasUpgrade ? GloasTestFixtures.BoundarySlot : 1, new EpochCache());
        byte[] advancedState = advancedCheckpoint == AdvancedCheckpoint.AcrossTheGloasUpgrade
            ? BeaconStateGloas.Encode(GloasForkTransition.UpgradeToGloas(advancedFulu, GloasCheckpointFiles.Spec))
            : BeaconStateFulu.Encode(advancedFulu);
        return (advancedState, new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain.AnchorBlock, Signature = new BlsSignature(new byte[BlsSignature.Length]) }),
            BeaconStateFulu.Encode(chain.AnchorState), chain.AnchorBlock.StateRoot!, advancedFulu.Slot);
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
