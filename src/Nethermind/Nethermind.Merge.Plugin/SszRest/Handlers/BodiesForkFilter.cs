// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Per execution-apis #793, <c>/engine/v2/{fork}/bodies/...</c> responses MUST mark
/// blocks whose timestamp falls outside the URL fork as <c>available=false</c>.
/// Applied at the SSZ-REST boundary because the underlying engine handler is shared
/// with the un-scoped JSON-RPC <c>engine_getPayloadBodies*</c> methods.
/// </summary>
internal static class BodiesForkFilter
{
    public static TResult?[] FilterByHash<TResult>(
        IReadOnlyList<TResult?> bodies,
        IReadOnlyList<Hash256> hashes,
        string urlFork,
        IBlockFinder blockFinder,
        ISpecProvider specProvider)
        where TResult : class
    {
        TResult?[] result = new TResult?[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            TResult? body = bodies[i];
            if (body is null) continue;
            BlockHeader? header = blockFinder.FindHeader(hashes[i]);
            if (header is not null && Matches(header, urlFork, specProvider))
                result[i] = body;
        }
        return result;
    }

    public static TResult?[] FilterByRange<TResult>(
        IReadOnlyList<TResult?> bodies,
        ulong start,
        string urlFork,
        IBlockFinder blockFinder,
        ISpecProvider specProvider)
        where TResult : class
    {
        TResult?[] result = new TResult?[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            TResult? body = bodies[i];
            if (body is null) continue;
            BlockHeader? header = blockFinder.FindHeader(start + (ulong)i);
            if (header is not null && Matches(header, urlFork, specProvider))
                result[i] = body;
        }
        return result;
    }

    private static bool Matches(BlockHeader header, string urlFork, ISpecProvider specProvider)
    {
        IReleaseSpec spec = specProvider.GetSpec(header);
        return string.Equals(SszRestPaths.GetEngineApiUrlSegment(spec), urlFork, StringComparison.OrdinalIgnoreCase);
    }
}
