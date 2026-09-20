// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Common;

internal readonly record struct ResolvedRawState(Hash256 Root, byte[] Ssz);
internal readonly record struct ResolvedState(Hash256 Root, BeaconStateFulu State);

/// <remarks>
/// Only a minority of roots have a persisted state: <see cref="Storage.BeaconChainStore"/> only
/// gets one written at finalization or on the periodic snapshot interval (see
/// <c>BlockImporter.PutState</c> call sites), not for every imported block. Resolving "head" for a
/// live, not-yet-finalized head will usually miss — that is reported as 503, not fabricated.
/// </remarks>
internal static class StateIdResolver
{
    /// <summary>Resolves to the raw, still-SSZ-encoded state bytes without decoding them.</summary>
    /// <remarks>Used by the debug SSZ endpoint, which streams the store's bytes as-is instead of paying to decode and re-encode.</remarks>
    public static bool TryResolveRaw(BeaconApiContext ctx, string stateId, out ResolvedRawState resolved, out int errorStatus, out string? errorMessage)
    {
        resolved = default;

        if (!IdResolver.TryResolveRoot(ctx, stateId, out Hash256? root, out errorStatus, out errorMessage))
        {
            return false;
        }

        if (!ctx.Store.TryGetState(root!, out byte[]? ssz))
        {
            errorStatus = StatusCodes.Status503ServiceUnavailable;
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

        BeaconStateFulu.Decode(raw.Ssz, out BeaconStateFulu state);
        resolved = new ResolvedState(raw.Root, state);
        return true;
    }
}
