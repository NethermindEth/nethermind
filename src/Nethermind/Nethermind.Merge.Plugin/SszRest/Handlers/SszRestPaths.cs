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
    private static readonly string _paris = Forks.Paris.Instance.EngineApiUrlSegment!;
    private static readonly string _shanghai = Forks.Shanghai.Instance.EngineApiUrlSegment!;
    private static readonly string _cancun = Forks.Cancun.Instance.EngineApiUrlSegment!;
    private static readonly string _prague = Forks.Prague.Instance.EngineApiUrlSegment!;
    private static readonly string _osaka = Forks.Osaka.Instance.EngineApiUrlSegment!;
    private static readonly string _amsterdam = Forks.Amsterdam.Instance.EngineApiUrlSegment!;

    /// <summary>
    /// Single source of truth: URL fork segment → <see cref="Forks.NamedReleaseSpec"/>. Built by
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
            result[spec.EngineApiUrlSegment!] = spec;
        return result;
    }

    public static readonly IReadOnlyList<string> SupportedForksOrdered = [.. _forkSpecByUrl.Keys];

    public static readonly FrozenSet<string> SupportedForks =
        SupportedForksOrdered.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cached <see cref="ReadOnlySpan{Char}"/> alternate lookup for <see cref="SupportedForks"/>,
    /// so the per-request <c>GetAlternateLookup</c> call in <c>SszMiddleware.TryRoute</c> is avoided.
    /// </summary>
    public static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> SupportedForksSpanLookup =
        SupportedForks.GetAlternateLookup<ReadOnlySpan<char>>();

    public const string Payloads = "payloads";

    public const string Forkchoice = "forkchoice";

    public const string Capabilities = "capabilities";

    public const string ClientVersion = "identity";

    public const string PayloadBodiesByHash = "bodies/hash";

    public const string PayloadBodiesByRange = "bodies";

    public const string Blobs = "blobs";

    // Witness endpoint resource segment (EIP-7928). The handler declares its own version-less
    // FixedPath, so SszMiddleware routes it by exact path rather than the fork-segment router.
    public const string NewPayloadWithWitness = "new-payload-with-witness";

    // Absolute request path the witness endpoint binds to (ISszEndpointHandler.FixedPath).
    public const string NewPayloadWithWitnessPath = "/" + NewPayloadWithWitness;

    // Documentation strings for the SSZ-REST routes — used by EngineRpcCapabilitiesProvider
    // (registration) and EngineModuleTests (coverage assertions). Built at static-init time from
    // each fork's EngineApiUrlSegment so the route docs stay in sync with the routing layer.
    public static readonly string PostV1Payloads = $"POST /engine/v2/{_paris}/payloads";
    public static readonly string GetV1Payloads = $"GET /engine/v2/{_paris}/payloads/{{payload_id}}";
    public static readonly string PostV1Forkchoice = $"POST /engine/v2/{_paris}/forkchoice";
    public const string PostV1Capabilities = "GET /engine/v2/capabilities";
    public const string PostV1ClientVersion = "GET /engine/v2/identity";

    public static readonly string PostV2Payloads = $"POST /engine/v2/{_shanghai}/payloads";
    public static readonly string PostV2Forkchoice = $"POST /engine/v2/{_shanghai}/forkchoice";
    public static readonly string GetV2Payloads = $"GET /engine/v2/{_shanghai}/payloads/{{payload_id}}";
    public static readonly string PostV1PayloadBodiesByHash = $"POST /engine/v2/{_shanghai}/bodies/hash";
    public static readonly string GetV1PayloadBodiesByRange = $"GET /engine/v2/{_shanghai}/bodies";

    public static readonly string PostV3Payloads = $"POST /engine/v2/{_cancun}/payloads";
    public static readonly string PostV3Forkchoice = $"POST /engine/v2/{_cancun}/forkchoice";
    public static readonly string GetV3Payloads = $"GET /engine/v2/{_cancun}/payloads/{{payload_id}}";
    public const string PostV1Blobs = "POST /engine/v2/blobs/v1";

    public static readonly string PostV4Payloads = $"POST /engine/v2/{_prague}/payloads";
    public static readonly string GetV4Payloads = $"GET /engine/v2/{_prague}/payloads/{{payload_id}}";

    public static readonly string GetV5Payloads = $"GET /engine/v2/{_osaka}/payloads/{{payload_id}}";
    public const string PostV2Blobs = "POST /engine/v2/blobs/v2";
    public const string PostV3Blobs = "POST /engine/v2/blobs/v3";

    public static readonly string PostV5Payloads = $"POST /engine/v2/{_amsterdam}/payloads";
    public static readonly string GetV6Payloads = $"GET /engine/v2/{_amsterdam}/payloads/{{payload_id}}";
    public static readonly string PostV4Forkchoice = $"POST /engine/v2/{_amsterdam}/forkchoice";
    public static readonly string PostV2PayloadBodiesByHash = $"POST /engine/v2/{_amsterdam}/bodies/hash";
    public static readonly string GetV2PayloadBodiesByRange = $"GET /engine/v2/{_amsterdam}/bodies";
    public const string PostV4Blobs = "POST /engine/v2/blobs/v4";

    /// <summary>
    /// Resolves the per-fork engine API method version for the given <paramref name="resource"/>
    /// + <paramref name="httpMethod"/> by looking it up on the fork's <see cref="Forks.NamedReleaseSpec"/>.
    /// Each fork only declares the versions it changes vs. its parent; values flow forward through
    /// the spec inheritance chain.
    /// </summary>
    public static int? MapForkToVersion(string fork, ReadOnlySpan<char> resource, string httpMethod)
    {
        if (!_forkSpecByUrl.TryGetValue(fork, out Forks.NamedReleaseSpec? spec)) return null;

        // Resource comparisons are case-insensitive (URL segments are lowercase per spec,
        // but routing accepts any case).
        if (string.Equals(httpMethod, "POST", StringComparison.Ordinal))
        {
            if (Eq(resource, Payloads)) return spec.EngineApiNewPayloadVersion;
            if (Eq(resource, Forkchoice)) return spec.EngineApiForkchoiceVersion;
            if (Eq(resource, PayloadBodiesByHash)) return spec.EngineApiPayloadBodiesByHashVersion;
        }
        else if (string.Equals(httpMethod, "GET", StringComparison.Ordinal))
        {
            if (Eq(resource, Payloads)) return spec.EngineApiGetPayloadVersion;
            if (Eq(resource, PayloadBodiesByRange)) return spec.EngineApiPayloadBodiesByRangeVersion;
        }

        return null;

        static bool Eq(ReadOnlySpan<char> a, string b) => a.Equals(b.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the URL fork segment that owns <paramref name="spec"/>'s engine API surface,
    /// walking up the parent chain so BPO forks resolve to their parent (e.g. <c>bpo1 → osaka</c>).
    /// </summary>
    public static string? GetEngineApiUrlSegment(IReleaseSpec spec)
    {
        for (Forks.NamedReleaseSpec? n = spec as Forks.NamedReleaseSpec; n is not null; n = n.Parent)
        {
            if (n.EngineApiUrlSegment is { } seg && _forkSpecByUrl.ContainsKey(seg))
                return seg;
        }
        return null;
    }
}

/// <summary>
/// Engine API capability names that are advertised by <c>engine_exchangeCapabilities</c> but are
/// not part of the standard <c>POST /engine/v2/{fork}/{resource}</c> path scheme. The EIP-7928
/// witness endpoint has its own dedicated, version-less path and is advertised under this name.
/// </summary>
public static class SszRestCapabilities
{
    public const string NewPayloadWithWitness = "rest_engine_newPayloadWithWitness";
}
