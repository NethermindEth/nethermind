// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Common;

internal readonly record struct ResolvedBlock(Hash256 Root, SignedBeaconBlock Block);

internal static class BlockIdResolver
{
    public static bool TryResolve(BeaconApiContext ctx, string blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage)
    {
        resolved = default;

        if (!IdResolver.TryResolveRoot(ctx, blockId, out Hash256? root, out errorStatus, out errorMessage))
        {
            return false;
        }

        if (!ctx.Store.TryGetBlock(root!, out SignedBeaconBlock? block))
        {
            errorStatus = StatusCodes.Status404NotFound;
            errorMessage = $"Block {root} is not retained by this node.";
            return false;
        }

        resolved = new ResolvedBlock(root!, block);
        return true;
    }
}
