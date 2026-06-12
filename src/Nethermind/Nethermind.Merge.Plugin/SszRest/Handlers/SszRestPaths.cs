// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Consensus;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public static class SszRestPaths
{
    public const string Paris = "paris";
    public const string Shanghai = "shanghai";
    public const string Cancun = "cancun";
    public const string Prague = "prague";
    public const string Osaka = "osaka";
    public const string Amsterdam = "amsterdam";

    public static readonly FrozenSet<string> SupportedForks =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Paris, Shanghai, Cancun, Prague, Osaka, Amsterdam
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cached <see cref="ReadOnlySpan{Char}"/> alternate lookup for <see cref="SupportedForks"/>,
    /// so the per-request <c>GetAlternateLookup</c> call in <c>SszMiddleware.TryRoute</c> is avoided.
    /// </summary>
    public static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> SupportedForksSpanLookup =
        SupportedForks.GetAlternateLookup<ReadOnlySpan<char>>();

    public static readonly IReadOnlyList<string> SupportedForksOrdered =
        [Paris, Shanghai, Cancun, Prague, Osaka, Amsterdam];

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
    /// Per-fork engine method versions. Adding a new fork is one row.
    /// </summary>
    private readonly record struct ForkVersions(
        int NewPayload, int GetPayload, int Forkchoice, int BodiesByHash, int BodiesByRange);

    private static readonly FrozenDictionary<string, ForkVersions> _forkVersions =
        new Dictionary<string, ForkVersions>(StringComparer.OrdinalIgnoreCase)
        {
            [Paris] = new(
                NewPayload: EngineApiVersions.NewPayload.V1,
                GetPayload: EngineApiVersions.GetPayload.V1,
                Forkchoice: EngineApiVersions.Fcu.V1,
                BodiesByHash: EngineApiVersions.PayloadBodiesByHash.V1,
                BodiesByRange: EngineApiVersions.PayloadBodiesByRange.V1),
            [Shanghai] = new(
                NewPayload: EngineApiVersions.NewPayload.V2,
                GetPayload: EngineApiVersions.GetPayload.V2,
                Forkchoice: EngineApiVersions.Fcu.V2,
                BodiesByHash: EngineApiVersions.PayloadBodiesByHash.V1,
                BodiesByRange: EngineApiVersions.PayloadBodiesByRange.V1),
            [Cancun] = new(
                NewPayload: EngineApiVersions.NewPayload.V3,
                GetPayload: EngineApiVersions.GetPayload.V3,
                Forkchoice: EngineApiVersions.Fcu.V3,
                BodiesByHash: EngineApiVersions.PayloadBodiesByHash.V1,
                BodiesByRange: EngineApiVersions.PayloadBodiesByRange.V1),
            [Prague] = new(
                NewPayload: EngineApiVersions.NewPayload.V4,
                GetPayload: EngineApiVersions.GetPayload.V4,
                Forkchoice: EngineApiVersions.Fcu.V3,
                BodiesByHash: EngineApiVersions.PayloadBodiesByHash.V1,
                BodiesByRange: EngineApiVersions.PayloadBodiesByRange.V1),
            [Osaka] = new(
                NewPayload: EngineApiVersions.NewPayload.V4,
                GetPayload: EngineApiVersions.GetPayload.V5,
                Forkchoice: EngineApiVersions.Fcu.V3,
                BodiesByHash: EngineApiVersions.PayloadBodiesByHash.V1,
                BodiesByRange: EngineApiVersions.PayloadBodiesByRange.V1),
            [Amsterdam] = new(
                NewPayload: EngineApiVersions.NewPayload.V5,
                GetPayload: EngineApiVersions.GetPayload.V6,
                Forkchoice: EngineApiVersions.Fcu.V4,
                BodiesByHash: EngineApiVersions.PayloadBodiesByHash.V2,
                BodiesByRange: EngineApiVersions.PayloadBodiesByRange.V2),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static int? MapForkToVersion(string fork, string resource, string httpMethod)
    {
        if (!_forkVersions.TryGetValue(fork, out ForkVersions v)) return null;

        // Resource comparisons are case-insensitive to match the previous behaviour
        // (URL segments are lowercase per spec, but routing accepts any case).
        if (string.Equals(httpMethod, "POST", StringComparison.Ordinal))
        {
            if (Eq(resource, Payloads)) return v.NewPayload;
            if (Eq(resource, Forkchoice)) return v.Forkchoice;
            if (Eq(resource, PayloadBodiesByHash)) return v.BodiesByHash;
        }
        else if (string.Equals(httpMethod, "GET", StringComparison.Ordinal))
        {
            if (Eq(resource, Payloads)) return v.GetPayload;
            if (Eq(resource, PayloadBodiesByRange)) return v.BodiesByRange;
        }

        return null;

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
