// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using Forks = Nethermind.Specs.Forks;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public static class SszRestPaths
{
    /// <summary>
    /// Single source of truth: fork name (Eth-Execution-Version value) → <see cref="Forks.NamedReleaseSpec"/>. Built by
    /// walking back from the latest fork via <see cref="Forks.NamedReleaseSpec.Parent"/> and
    /// keeping every fork that introduces an engine-API method-version change vs. its parent.
    /// BPO blob-parameter override forks inherit all engine-API versions and so are filtered out.
    /// </summary>
    /// <remarks>
    /// To add support for a new fork, add it as a <see cref="Forks.NamedReleaseSpec"/> with its
    /// engine-API version overrides and update the <c>latest</c> argument here.
    /// </remarks>
    private static readonly Dictionary<string, Forks.NamedReleaseSpec> _forkSpecByUrl =
        BuildForkSpecsByUrl(Forks.Amsterdam.Instance);

    private static Dictionary<string, Forks.NamedReleaseSpec> BuildForkSpecsByUrl(Forks.NamedReleaseSpec latest)
    {
        // Stack reverses parent-chain order (Amsterdam → … → Paris becomes Paris → … → Amsterdam),
        // so the resulting dictionary preserves chronological insertion order.
        Stack<Forks.NamedReleaseSpec> ordered = new();
        for (Forks.NamedReleaseSpec? spec = latest; spec?.EngineApiNewPayloadVersion is not null; spec = spec.Parent)
        {
            if (spec.IntroducesEngineApiChange())
                ordered.Push(spec);
        }

        Dictionary<string, Forks.NamedReleaseSpec> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (Forks.NamedReleaseSpec spec in ordered)
            result[spec.EngineApiForkName!] = spec;
        return result;
    }

    public static readonly IReadOnlyList<string> SupportedForksOrdered = [.. _forkSpecByUrl.Keys];

    public static readonly FrozenSet<string> SupportedForks =
        SupportedForksOrdered.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public const string Payloads = "payloads";

    public const string Forkchoice = "forkchoice";

    public const string Capabilities = "capabilities";

    public const string ClientVersion = "identity";

    public const string PayloadBodiesByHash = "bodies/hash";

    public const string PayloadBodiesByRange = "bodies";

    public const string Blobs = "blobs";

    // Documentation strings for the SSZ-REST routes — used by EngineRpcCapabilitiesProvider
    // (registration) and EngineModuleTests (coverage assertions). Since execution-apis#793 moved
    // the fork out of the path and into the Eth-Execution-Version header, each fork-scoped route is
    // advertised once: fork selection is a request header, not a distinct path. Blobs remain
    // independently path-versioned, and identity/capabilities stay unscoped.
    public const string PostPayloads = "POST /engine/v2/payloads";
    public const string GetPayloads = "GET /engine/v2/payloads/{payload_id}";
    public const string PostForkchoice = "POST /engine/v2/forkchoice";
    public const string PostBodiesByHash = "POST /engine/v2/bodies/hash";
    public const string GetBodiesByRange = "GET /engine/v2/bodies";
    public const string GetCapabilities = "GET /engine/v2/capabilities";
    public const string GetIdentity = "GET /engine/v2/identity";
    public const string PostBlobsV1 = "POST /engine/v2/blobs/v1";
    public const string PostBlobsV2 = "POST /engine/v2/blobs/v2";
    public const string PostBlobsV3 = "POST /engine/v2/blobs/v3";
    public const string PostBlobsV4 = "POST /engine/v2/blobs/v4";

    /// <summary>
    /// Resolves the per-fork engine API method version for the given <paramref name="resource"/>
    /// + <paramref name="httpMethod"/> by looking it up on the fork's <see cref="Forks.NamedReleaseSpec"/>.
    /// Each fork only declares the versions it changes vs. its parent; values flow forward through
    /// the spec inheritance chain.
    /// </summary>
    /// <param name="recognizedResource">
    /// <c>true</c> when <paramref name="resource"/> + <paramref name="httpMethod"/> name a known
    /// fork-scoped endpoint, even if this fork has no version for it (e.g. <c>paris</c> + <c>bodies</c>).
    /// Lets the caller distinguish "endpoint not available for this fork" (400) from "unknown
    /// endpoint" (404) when the returned version is <c>null</c>.
    /// </param>
    public static int? MapForkToVersion(string fork, ReadOnlySpan<char> resource, string httpMethod, out bool recognizedResource)
    {
        recognizedResource = false;
        if (!_forkSpecByUrl.TryGetValue(fork, out Forks.NamedReleaseSpec? spec)) return null;

        // Resource comparisons are case-insensitive (fork names are lowercase per spec,
        // but routing accepts any case).
        if (string.Equals(httpMethod, "POST", StringComparison.Ordinal))
        {
            if (Eq(resource, Payloads)) { recognizedResource = true; return spec.EngineApiNewPayloadVersion; }
            if (Eq(resource, Forkchoice)) { recognizedResource = true; return spec.EngineApiForkchoiceVersion; }
            if (Eq(resource, PayloadBodiesByHash)) { recognizedResource = true; return spec.EngineApiPayloadBodiesByHashVersion; }
        }
        else if (string.Equals(httpMethod, "GET", StringComparison.Ordinal))
        {
            if (Eq(resource, Payloads)) { recognizedResource = true; return spec.EngineApiGetPayloadVersion; }
            if (Eq(resource, PayloadBodiesByRange)) { recognizedResource = true; return spec.EngineApiPayloadBodiesByRangeVersion; }
        }

        return null;

        static bool Eq(ReadOnlySpan<char> a, string b) => a.Equals(b.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the fork name (<c>Eth-Execution-Version</c> value) that owns <paramref name="spec"/>'s
    /// engine API surface, walking up the parent chain so BPO forks resolve to their parent
    /// (e.g. <c>bpo1 → osaka</c>).
    /// </summary>
    public static string? GetEngineApiForkName(IReleaseSpec spec)
    {
        for (Forks.NamedReleaseSpec? n = spec as Forks.NamedReleaseSpec; n is not null; n = n.Parent)
        {
            if (n.EngineApiForkName is { } forkName && _forkSpecByUrl.ContainsKey(forkName))
                return forkName;
        }
        return null;
    }
}
