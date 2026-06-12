// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Specs.Forks;
using Forks = Nethermind.Specs.Forks;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public static class SszRestPaths
{
    public const string Paris = "paris";
    public const string Shanghai = "shanghai";
    public const string Cancun = "cancun";
    public const string Prague = "prague";
    public const string Osaka = "osaka";
    public const string Amsterdam = "amsterdam";

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

    // Paris
    public const string PostV1Payloads = "POST /engine/v2/" + Paris + "/payloads";
    public const string GetV1Payloads = "GET /engine/v2/" + Paris + "/payloads/{payload_id}";
    public const string PostV1Forkchoice = "POST /engine/v2/" + Paris + "/forkchoice";
    public const string PostV1Capabilities = "GET /engine/v2/capabilities";
    public const string PostV1ClientVersion = "GET /engine/v2/identity";

    // Shanghai
    public const string PostV2Payloads = "POST /engine/v2/" + Shanghai + "/payloads";
    public const string PostV2Forkchoice = "POST /engine/v2/" + Shanghai + "/forkchoice";
    public const string GetV2Payloads = "GET /engine/v2/" + Shanghai + "/payloads/{payload_id}";
    public const string PostV1PayloadBodiesByHash = "POST /engine/v2/" + Shanghai + "/bodies/hash";
    public const string GetV1PayloadBodiesByRange = "GET /engine/v2/" + Shanghai + "/bodies";

    // Cancun
    public const string PostV3Payloads = "POST /engine/v2/" + Cancun + "/payloads";
    public const string PostV3Forkchoice = "POST /engine/v2/" + Cancun + "/forkchoice";
    public const string GetV3Payloads = "GET /engine/v2/" + Cancun + "/payloads/{payload_id}";
    public const string PostV1Blobs = "POST /engine/v2/blobs/v1";

    // Prague
    public const string PostV4Payloads = "POST /engine/v2/" + Prague + "/payloads";
    public const string GetV4Payloads = "GET /engine/v2/" + Prague + "/payloads/{payload_id}";

    // Osaka
    public const string GetV5Payloads = "GET /engine/v2/" + Osaka + "/payloads/{payload_id}";
    public const string PostV2Blobs = "POST /engine/v2/blobs/v2";
    public const string PostV3Blobs = "POST /engine/v2/blobs/v3";

    // Amsterdam
    public const string PostV5Payloads = "POST /engine/v2/" + Amsterdam + "/payloads";
    public const string GetV6Payloads = "GET /engine/v2/" + Amsterdam + "/payloads/{payload_id}";
    public const string PostV4Forkchoice = "POST /engine/v2/" + Amsterdam + "/forkchoice";
    public const string PostV2PayloadBodiesByHash = "POST /engine/v2/" + Amsterdam + "/bodies/hash";
    public const string GetV2PayloadBodiesByRange = "GET /engine/v2/" + Amsterdam + "/bodies";
    public const string PostV4Blobs = "POST /engine/v2/blobs/v4";

    /// <summary>
    /// Resolves the per-fork engine API method version for the given <paramref name="resource"/>
    /// + <paramref name="httpMethod"/> by looking it up on the fork's <see cref="Forks.NamedReleaseSpec"/>.
    /// Each fork only declares the versions it changes vs. its parent; values flow forward through
    /// the spec inheritance chain.
    /// </summary>
    public static int? MapForkToVersion(string fork, string resource, string httpMethod)
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

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
