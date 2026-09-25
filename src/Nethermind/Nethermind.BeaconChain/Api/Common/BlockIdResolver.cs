// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Common;

internal readonly record struct ResolvedBlock(Hash256 Root, ForkedSignedBeaconBlock Block)
{
    public ulong Slot => Block.Slot;

    public Hash256 StateRoot => Block switch
    {
        ForkedSignedBeaconBlock.OfFulu fulu => fulu.Block.Message!.StateRoot!,
        ForkedSignedBeaconBlock.OfGloas gloas => gloas.Block.Message!.StateRoot!,
        _ => throw UnknownShape(),
    };

    public BlsSignature Signature => Block switch
    {
        ForkedSignedBeaconBlock.OfFulu fulu => fulu.Block.Signature,
        ForkedSignedBeaconBlock.OfGloas gloas => gloas.Block.Signature,
        _ => throw UnknownShape(),
    };

    public Hash256 ComputeBodyRoot() => Block switch
    {
        ForkedSignedBeaconBlock.OfFulu fulu => SszRoots.HashTreeRoot(fulu.Block.Message!.Body!),
        ForkedSignedBeaconBlock.OfGloas gloas => SszRoots.HashTreeRoot(gloas.Block.Message!.Body!),
        _ => throw UnknownShape(),
    };

    private NotSupportedException UnknownShape() => new($"Unhandled signed beacon block shape {Block.GetType().Name}");
}

internal static class BlockIdResolver
{
    public static bool TryResolve(BeaconApiContext ctx, string blockId, out ResolvedBlock resolved, out int errorStatus, out string? errorMessage)
    {
        resolved = default;

        if (!IdResolver.TryResolveRoot(ctx, blockId, out Hash256? root, out errorStatus, out errorMessage))
        {
            return false;
        }

        if (!ctx.Store.TryGetForkedBlock(root!, out ForkedSignedBeaconBlock? block))
        {
            errorStatus = StatusCodes.Status404NotFound;
            errorMessage = $"Block {root} is not retained by this node.";
            return false;
        }

        resolved = new ResolvedBlock(root!, block);
        return true;
    }
}
