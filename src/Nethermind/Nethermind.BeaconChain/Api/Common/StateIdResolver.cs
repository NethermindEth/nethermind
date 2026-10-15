// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Common;

internal readonly record struct ResolvedRawState(Hash256 Root, byte[] Ssz);
internal readonly record struct ResolvedState(Hash256 Root, ApiState State);

internal static class StateIdResolver
{
    private const string HeadStateNotReady = "The head state is not ready yet; retry shortly.";

    /// <summary>The <c>fork</c> field of a state at <paramref name="epoch"/>: the upgrade functions set it to the live version, the one before it and the epoch it went live.</summary>
    private static Fork ForkAt(BeaconChainSpec spec, ulong epoch)
    {
        ForkScheduleEntry[] forks = spec.Forks;
        int live = forks.Length - 1;
        while (live > 0 && forks[live].Epoch > epoch) live--;
        while (live > 0 && forks[live - 1].Version.AsSpan().SequenceEqual(forks[live].Version)) live--;
        return new Fork { PreviousVersion = forks[live > 0 ? live - 1 : 0].Version, CurrentVersion = forks[live].Version, Epoch = forks[live].Epoch };
    }

    public static bool TryResolveBlock(BeaconApiContext ctx, string stateId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage)
    {
        resolved = default;
        if (!TryResolveRoot(ctx, stateId, out Hash256? root, out errorStatus, out errorMessage)) return false;
        return BlockIdResolver.TryResolve(ctx, root!.ToString(), out resolved, out errorStatus, out errorMessage);
    }

    /// <summary>Whether <paramref name="stateId"/> resolves to the head of the request's get_head view.</summary>
    internal static bool NamesHead(BeaconApiContext ctx, string stateId) =>
        TryResolveRoot(ctx, stateId, out Hash256? root, out _, out _) && root == ctx.StatusSource.CurrentStatus.HeadRoot;

    private static bool TryResolveRoot(BeaconApiContext ctx, string stateId, out Hash256? root, out int errorStatus, out string? errorMessage)
    {
        // params/index.yaml StateId: a hexadecimal identifier names a state commitment, not a block.
        if (!HexConvert.TryParseHash(stateId, out Hash256? stateRoot))
            return IdResolver.TryResolveRoot(ctx, stateId, out root, out errorStatus, out errorMessage);

        root = null;
        errorStatus = StatusCodes.Status404NotFound;
        errorMessage = $"No block with state root {stateRoot} is retained by this node.";
        if (!ctx.Store.TryGetBlockRootByStateRoot(stateRoot!, out root)) return false;
        errorStatus = 0;
        errorMessage = null;
        return true;
    }

    /// <summary>Resolves to the raw, still-SSZ-encoded state bytes without decoding them.</summary>
    /// <remarks>Used by the debug SSZ endpoint, which streams the store's bytes as-is instead of paying to decode and re-encode.</remarks>
    public static bool TryResolveRaw(BeaconApiContext ctx, string stateId, out ResolvedRawState resolved, out int errorStatus, out string? errorMessage)
    {
        resolved = default;

        if (!TryResolveRoot(ctx, stateId, out Hash256? root, out errorStatus, out errorMessage))
        {
            return false;
        }

        byte[]? ssz = root == ctx.StatusSource.CurrentStatus.HeadRoot ? ctx.HeadState switch
        {
            ForkedBeaconState.OfFulu fulu => BeaconStateFulu.Encode(fulu.State),
            ForkedBeaconState.OfGloas gloas => BeaconStateGloas.Encode(gloas.State),
            _ => null,
        } : null;
        if (ssz is null && root == ctx.StatusSource.CurrentStatus.HeadRoot && ctx.DeferredHeadState is { } deferred)
        {
            // The head's state is taken off this thread; a later request finds it ready instead of waiting here.
            _ = deferred.TakeAsync();
            errorStatus = StatusCodes.Status503ServiceUnavailable;
            errorMessage = HeadStateNotReady;
            return false;
        }

        if (ssz is null && !ctx.Store.TryGetState(root!, out ssz))
        {
            errorStatus = StatusCodes.Status404NotFound;
            errorMessage = $"No state is persisted for '{stateId}' ({root}): this node retains persisted states at finalization.";
            return false;
        }

        resolved = new ResolvedRawState(root!, ssz);
        return true;
    }

    /// <summary>Resolves <paramref name="stateId"/> to the already-materialized head state, without encoding or decoding it; <c>false</c> when it names any other state or does not resolve.</summary>
    public static bool TryResolveHead(BeaconApiContext ctx, string stateId, out ResolvedState resolved)
    {
        resolved = default;
        return TryResolveRoot(ctx, stateId, out Hash256? root, out _, out _)
            && root == ctx.StatusSource.CurrentStatus.HeadRoot
            && ctx.HeadState is not null
            && TryResolve(ctx, stateId, out resolved, out _, out _);
    }

    internal static bool TryResolvePrefix(BeaconApiContext ctx, string stateId, bool forkOnly, out ResolvedState resolved, out int errorStatus, out string? errorMessage, bool includeRandao = false)
    {
        resolved = default;
        if (!TryResolveRoot(ctx, stateId, out Hash256? root, out errorStatus, out errorMessage)) return false;
        if (root == ctx.StatusSource.CurrentStatus.HeadRoot && ctx.HeadState is not null)
            return TryResolve(ctx, stateId, out resolved, out errorStatus, out errorMessage);

        // A fork read never waits for the head's state: the head's fork follows from its slot and the fork schedule.
        if (forkOnly && root == ctx.StatusSource.CurrentStatus.HeadRoot && ctx.DeferredHeadState is not null)
        {
            ulong headSlot = ctx.StatusSource.CurrentStatus.HeadSlot;
            ulong headEpoch = ctx.Spec.GetEpoch(headSlot);
            if (headEpoch < ctx.Spec.ElectraForkEpoch)
                throw new UnsupportedForkException(new NotSupportedException("State predates Electra."));
            BeaconStateElectra head = new() { Slot = headSlot, Fork = ForkAt(ctx.Spec, headEpoch) };
            resolved = new ResolvedState(root!, ApiState.Of(headEpoch >= ctx.Spec.GloasForkEpoch ? BeaconFork.Gloas : headEpoch >= ctx.Spec.FuluForkEpoch ? BeaconFork.Fulu : BeaconFork.Electra, head));
            return true;
        }

        const int forkOffset = 48;
        int randaoOffset = checked(176 + 64 * (int)Presets.SlotsPerHistoricalRoot + 96);
        int checkpointOffset = checked(randaoOffset + 32 * (int)Presets.EpochsPerHistoricalVector + 8 * (int)Presets.EpochsPerSlashingsVector + 9);
        int length = forkOnly ? 176 : checkpointOffset + 120;
        if (!ctx.Store.TryGetStatePrefix(root!, length, out byte[]? prefix))
        {
            if (ctx.Store.TryGetStatePrefix(root!, 48, out byte[]? headerBytes)
                && ctx.Spec.GetEpoch(BinaryPrimitives.ReadUInt64LittleEndian(headerBytes.AsSpan(40))) < ctx.Spec.ElectraForkEpoch)
                throw new UnsupportedForkException(new NotSupportedException("State predates Electra."));
            errorStatus = StatusCodes.Status404NotFound;
            errorMessage = $"No state is retained for '{stateId}'.";
            return false;
        }
        ReadOnlySpan<byte> bytes = prefix;
        ulong slot = BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]);
        ulong epoch = ctx.Spec.GetEpoch(slot);
        if (epoch < ctx.Spec.ElectraForkEpoch)
            throw new UnsupportedForkException(new NotSupportedException("State predates Electra."));
        Fork.Decode(bytes.Slice(forkOffset, 16), out Fork fork);
        BeaconBlockHeader.Decode(bytes.Slice(64, 112), out BeaconBlockHeader header);
        BeaconStateElectra state = new() { Slot = slot, Fork = fork, LatestBlockHeader = header };
        if (!forkOnly)
        {
            if (includeRandao)
            {
                state.RandaoMixes = new Hash256[Presets.EpochsPerHistoricalVector];
                for (int i = 0; i < state.RandaoMixes.Length; i++)
                    state.RandaoMixes[i] = new Hash256(bytes.Slice(randaoOffset + i * 32, 32));
            }
            Checkpoint.Decode(bytes.Slice(checkpointOffset, 40), out Checkpoint previous);
            Checkpoint.Decode(bytes.Slice(checkpointOffset + 40, 40), out Checkpoint current);
            Checkpoint.Decode(bytes.Slice(checkpointOffset + 80, 40), out Checkpoint finalized);
            state.PreviousJustifiedCheckpoint = previous;
            state.CurrentJustifiedCheckpoint = current;
            state.FinalizedCheckpoint = finalized;
        }
        resolved = new ResolvedState(root!, ApiState.Of(epoch >= ctx.Spec.GloasForkEpoch ? BeaconFork.Gloas : epoch >= ctx.Spec.FuluForkEpoch ? BeaconFork.Fulu : BeaconFork.Electra, state));
        return true;
    }

    public static bool TryResolve(BeaconApiContext ctx, string stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage)
    {
        resolved = default;
        if (!TryResolveRoot(ctx, stateId, out Hash256? root, out errorStatus, out errorMessage)) return false;
        if (root == ctx.StatusSource.CurrentStatus.HeadRoot && ctx.HeadState is { } head)
        {
            ApiState current = head switch
            {
                ForkedBeaconState.OfFulu fulu => ApiState.Of(BeaconFork.Fulu, fulu.State),
                ForkedBeaconState.OfGloas gloas => ApiState.Of(gloas.State),
                _ => throw new NotSupportedException(),
            };
            resolved = new ResolvedState(root!, current);
            return true;
        }
        if (!TryResolveRaw(ctx, stateId, out ResolvedRawState raw, out errorStatus, out errorMessage))
        {
            return false;
        }

        ApiState state = ApiStateDecoding.Decode(raw.Ssz, ctx.Spec);
        resolved = new ResolvedState(raw.Root, state);
        return true;
    }
}
