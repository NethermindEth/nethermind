// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Http;
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

        if (!ctx.Store.TryGetState(root!, out byte[]? ssz))
        {
            errorStatus = StatusCodes.Status404NotFound;
            errorMessage = $"No state is persisted for '{stateId}' ({root}): this node only retains states at finalization or on the periodic snapshot interval.";
            return false;
        }

        resolved = new ResolvedRawState(root!, ssz);
        return true;
    }

    public static bool TryResolve(BeaconApiContext ctx, string stateId, out ResolvedState resolved, out int errorStatus, out string? errorMessage)
    {
        resolved = default;
        if (!TryResolveRaw(ctx, stateId, out ResolvedRawState raw, out errorStatus, out errorMessage))
        {
            return false;
        }

        ApiState state = ApiStateDecoding.Decode(raw.Ssz, ctx.Spec);
        resolved = new ResolvedState(raw.Root, state);
        return true;
    }
}
