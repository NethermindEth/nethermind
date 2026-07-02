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
            result[ForkName(spec)!] = spec;
        return result;
    }

    /// <summary>Lowercase fork name used as the <c>Eth-Execution-Version</c> header value.</summary>
    private static string? ForkName(Forks.NamedReleaseSpec spec) => spec.Name?.ToLowerInvariant();

    private static bool ResourceEquals(ReadOnlySpan<char> resource, string name) =>
        resource.Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase);

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

    /// <summary>How a resource's fork and version are determined by <c>SszMiddleware</c>.</summary>
    public enum ResourceScoping
    {
        /// <summary>Fork (and thus method version) comes from the <c>Eth-Execution-Version</c> header.</summary>
        ForkScoped,
        /// <summary>Version comes from a trailing <c>/v{N}</c> path segment; no fork header (blobs).</summary>
        PathVersioned,
        /// <summary>No fork and no version — a single endpoint (capabilities, identity).</summary>
        Unscoped
    }

    /// <summary>Classifies how the given first path <paramref name="resource"/> segment is routed.</summary>
    public static ResourceScoping GetScoping(ReadOnlySpan<char> resource)
    {
        if (ResourceEquals(resource, Capabilities) || ResourceEquals(resource, ClientVersion)) return ResourceScoping.Unscoped;
        if (ResourceEquals(resource, Blobs)) return ResourceScoping.PathVersioned;
        return ResourceScoping.ForkScoped;
    }

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
        ForkScopedMethod method = ClassifyMethod(resource, httpMethod);
        recognizedResource = method != ForkScopedMethod.None;
        if (!recognizedResource || !_forkSpecByUrl.TryGetValue(fork, out Forks.NamedReleaseSpec? spec))
            return null;

        return method switch
        {
            ForkScopedMethod.NewPayload => spec.EngineApiNewPayloadVersion,
            ForkScopedMethod.GetPayload => spec.EngineApiGetPayloadVersion,
            ForkScopedMethod.Forkchoice => spec.EngineApiForkchoiceVersion,
            ForkScopedMethod.BodiesByHash => spec.EngineApiPayloadBodiesByHashVersion,
            ForkScopedMethod.BodiesByRange => spec.EngineApiPayloadBodiesByRangeVersion,
            _ => null
        };
    }

    private enum ForkScopedMethod { None, NewPayload, GetPayload, Forkchoice, BodiesByHash, BodiesByRange }

    /// <summary>Maps a fork-scoped (<paramref name="resource"/>, <paramref name="httpMethod"/>) pair
    /// to the engine method it targets, or <see cref="ForkScopedMethod.None"/> if unrecognized.</summary>
    private static ForkScopedMethod ClassifyMethod(ReadOnlySpan<char> resource, string httpMethod)
    {
        // Resource comparisons are case-insensitive (fork/resource names are lowercase per spec,
        // but routing accepts any case).
        if (string.Equals(httpMethod, "POST", StringComparison.Ordinal))
        {
            if (ResourceEquals(resource, Payloads)) return ForkScopedMethod.NewPayload;
            if (ResourceEquals(resource, Forkchoice)) return ForkScopedMethod.Forkchoice;
            if (ResourceEquals(resource, PayloadBodiesByHash)) return ForkScopedMethod.BodiesByHash;
        }
        else if (string.Equals(httpMethod, "GET", StringComparison.Ordinal))
        {
            if (ResourceEquals(resource, Payloads)) return ForkScopedMethod.GetPayload;
            if (ResourceEquals(resource, PayloadBodiesByRange)) return ForkScopedMethod.BodiesByRange;
        }
        return ForkScopedMethod.None;
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
            if (ForkName(n) is { } forkName && _forkSpecByUrl.ContainsKey(forkName))
                return forkName;
        }
        return null;
    }
}
