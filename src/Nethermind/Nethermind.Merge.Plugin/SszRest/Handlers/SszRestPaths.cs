// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

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

    public static int ForkOrdinal(string forkUrl)
    {
        for (int i = 0; i < SupportedForksOrdered.Count; i++)
        {
            if (string.Equals(SupportedForksOrdered[i], forkUrl, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

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

    private static readonly FrozenDictionary<ForkVersionKey, int> _forkVersionMap =
        new Dictionary<ForkVersionKey, int>
        {
            // newPayload (POST payloads)
            [new(Paris, Payloads, "POST")] = 1,
            [new(Shanghai, Payloads, "POST")] = 2,
            [new(Cancun, Payloads, "POST")] = 3,
            [new(Prague, Payloads, "POST")] = 4,
            [new(Osaka, Payloads, "POST")] = 4,
            [new(Amsterdam, Payloads, "POST")] = 5,

            // getPayload (GET payloads)
            [new(Paris, Payloads, "GET")] = 1,
            [new(Shanghai, Payloads, "GET")] = 2,
            [new(Cancun, Payloads, "GET")] = 3,
            [new(Prague, Payloads, "GET")] = 4,
            [new(Osaka, Payloads, "GET")] = 5,
            [new(Amsterdam, Payloads, "GET")] = 6,

            // forkchoiceUpdated (POST forkchoice)
            [new(Paris, Forkchoice, "POST")] = 1,
            [new(Shanghai, Forkchoice, "POST")] = 2,
            [new(Cancun, Forkchoice, "POST")] = 3,
            [new(Prague, Forkchoice, "POST")] = 3,
            [new(Osaka, Forkchoice, "POST")] = 3,
            [new(Amsterdam, Forkchoice, "POST")] = 4,

            // bodies/hash (POST)
            [new(Paris, PayloadBodiesByHash, "POST")] = 1,
            [new(Shanghai, PayloadBodiesByHash, "POST")] = 1,
            [new(Cancun, PayloadBodiesByHash, "POST")] = 1,
            [new(Prague, PayloadBodiesByHash, "POST")] = 1,
            [new(Osaka, PayloadBodiesByHash, "POST")] = 1,
            [new(Amsterdam, PayloadBodiesByHash, "POST")] = 2,

            // bodies (GET)
            [new(Paris, PayloadBodiesByRange, "GET")] = 1,
            [new(Shanghai, PayloadBodiesByRange, "GET")] = 1,
            [new(Cancun, PayloadBodiesByRange, "GET")] = 1,
            [new(Prague, PayloadBodiesByRange, "GET")] = 1,
            [new(Osaka, PayloadBodiesByRange, "GET")] = 1,
            [new(Amsterdam, PayloadBodiesByRange, "GET")] = 2,
        }.ToFrozenDictionary();

    public static int? MapForkToVersion(string fork, string resource, string httpMethod) =>
        _forkVersionMap.TryGetValue(new ForkVersionKey(fork, resource, httpMethod), out int version)
            ? version
            : null;

    /// <summary>
    /// Key for <c>_forkVersionMap</c>: fork name and resource compared case-insensitively; HTTP
    /// method ordinally.
    /// </summary>
    private readonly record struct ForkVersionKey(string Fork, string Resource, string Method)
    {
        public bool Equals(ForkVersionKey other) =>
            string.Equals(Fork, other.Fork, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Resource, other.Resource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Method, other.Method, StringComparison.Ordinal);

        public override int GetHashCode() =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(Fork),
                StringComparer.OrdinalIgnoreCase.GetHashCode(Resource),
                Method);
    }
}
