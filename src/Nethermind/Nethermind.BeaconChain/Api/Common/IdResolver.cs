// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>
/// Resolves a beacon-api <c>block_id</c>/<c>state_id</c> path parameter to a block root. Shared by
/// every endpoint that takes one of these identifiers, so "head"/"finalized"/"justified"/"genesis"/slot/root
/// parsing is defined exactly once.
/// </summary>
internal static class IdResolver
{
    public static bool TryResolveRoot(BeaconChainStore store, BeaconApiHead view, string id, out Hash256? root, out int errorStatus, out string? errorMessage)
    {
        root = null;
        errorStatus = 0;
        errorMessage = null;

        switch (id)
        {
            case "head":
                root = view.StatusSource.CurrentStatus.HeadRoot;
                if (root == Hash256.Zero)
                {
                    root = null;
                    errorStatus = StatusCodes.Status503ServiceUnavailable;
                    errorMessage = "The driver has not established a head yet.";
                    return false;
                }
                return true;

            case "finalized":
                root = view.StatusSource.CurrentStatus.FinalizedRoot;
                if (root == Hash256.Zero)
                {
                    root = null;
                    errorStatus = StatusCodes.Status503ServiceUnavailable;
                    errorMessage = "The driver has not established a finalized checkpoint yet.";
                    return false;
                }
                return true;

            case "genesis":
                if (!store.TryGetCanonicalRoot(0, out root))
                {
                    errorStatus = StatusCodes.Status404NotFound;
                    errorMessage = "The genesis block is not retained by this node: it checkpoint-synced from a later finalized state.";
                    return false;
                }
                return true;

            case "justified":
                root = view.StatusSource.JustifiedRoot;
                if (root == Hash256.Zero)
                {
                    root = null;
                    errorStatus = StatusCodes.Status503ServiceUnavailable;
                    errorMessage = "The driver has not established a justified checkpoint yet.";
                    return false;
                }
                return true;

            default:
                if (ulong.TryParse(id, out ulong slot))
                {
                    if (!store.TryGetCanonicalRoot(slot, out root))
                    {
                        errorStatus = StatusCodes.Status404NotFound;
                        errorMessage = $"No canonical block is retained at slot {slot}.";
                        return false;
                    }
                    return true;
                }

                if (HexConvert.TryParseHash(id, out root))
                {
                    return true;
                }

                errorStatus = StatusCodes.Status400BadRequest;
                errorMessage = "Invalid id: expected 'head', 'finalized', 'justified', 'genesis', a slot number, or a 0x-prefixed 32-byte root.";
                return false;
        }
    }
}
