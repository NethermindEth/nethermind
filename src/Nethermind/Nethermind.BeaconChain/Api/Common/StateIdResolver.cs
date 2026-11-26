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
    public static bool TryResolveBlock(BeaconApiContext ctx, string stateId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage)
    {
        resolved = default;
        if (!TryResolveRoot(ctx, stateId, out Hash256? root, out errorStatus, out errorMessage)) return false;
        return BlockIdResolver.TryResolve(ctx, root!.ToString(), out resolved, out errorStatus, out errorMessage);
    }

    private static bool TryResolveRoot(BeaconApiContext ctx, string stateId, out Hash256? root, out int errorStatus, out string? errorMessage)
    {
        if (ctx.GetCheckpointState(stateId) is { } checkpoint)
        {
            root = checkpoint.Checkpoint.Root;
            errorStatus = 0;
            errorMessage = null;
            return true;
        }
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

        byte[]? ssz = ctx.GetHeldState(root!, stateId) switch
        {
            ForkedBeaconState.OfFulu fulu => BeaconStateFulu.Encode(fulu.State),
            ForkedBeaconState.OfGloas gloas => BeaconStateGloas.Encode(gloas.State),
            _ => null,
        };
        if (ssz is null && !ctx.Store.TryGetState(root!, out ssz))
        {
            errorStatus = StatusCodes.Status404NotFound;
            errorMessage = $"No state is persisted for '{stateId}' ({root}): this node retains persisted states at finalization.";
            return false;
        }

        resolved = new ResolvedRawState(root!, ssz);
        return true;
    }

    /// <summary>Resolves <paramref name="stateId"/> to the already-materialized head or justified state, without encoding or decoding it; <c>false</c> when it names any other state or does not resolve.</summary>
    public static bool TryResolveHeld(BeaconApiContext ctx, string stateId, out ResolvedState resolved)
    {
        resolved = default;
        return TryResolveRoot(ctx, stateId, out Hash256? root, out _, out _)
            && ctx.GetHeldState(root!, stateId) is not null
            && TryResolve(ctx, stateId, out resolved, out _, out _);
    }

    internal const int HistoricalRootsOffset = 176 + 64 * (int)Presets.SlotsPerHistoricalRoot;
    internal const int RandaoOffset = HistoricalRootsOffset + 96;
    internal const int ParticipationOffset = RandaoOffset + 32 * (int)Presets.EpochsPerHistoricalVector + 8 * (int)Presets.EpochsPerSlashingsVector;

    private const int CheckpointOffset = ParticipationOffset + 9;
    internal const int CheckpointsEnd = CheckpointOffset + 120;

    internal static bool TryResolvePrefix(BeaconApiContext ctx, string stateId, bool forkOnly, out ResolvedState resolved, out int errorStatus, out string? errorMessage) =>
        TryResolvePrefix(ctx, stateId, forkOnly, out resolved, out errorStatus, out errorMessage, out _);

    internal static bool TryResolvePrefix(BeaconApiContext ctx, string stateId, bool forkOnly, out ResolvedState resolved, out int errorStatus, out string? errorMessage, out byte[]? prefix)
    {
        prefix = null;
        resolved = default;
        if (!TryResolveRoot(ctx, stateId, out Hash256? root, out errorStatus, out errorMessage)) return false;
        if (ctx.GetHeldState(root!, stateId) is not null)
            return TryResolve(ctx, stateId, out resolved, out errorStatus, out errorMessage);

        const int forkOffset = 48;
        int length = forkOnly ? 176 : CheckpointsEnd;
        if (!ctx.Store.TryGetStatePrefix(root!, length, out prefix))
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
            Checkpoint.Decode(bytes.Slice(CheckpointOffset, 40), out Checkpoint previous);
            Checkpoint.Decode(bytes.Slice(CheckpointOffset + 40, 40), out Checkpoint current);
            Checkpoint.Decode(bytes.Slice(CheckpointOffset + 80, 40), out Checkpoint finalized);
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
        if (ctx.GetHeldState(root!, stateId) is { } held)
        {
            ApiState current = held switch
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
