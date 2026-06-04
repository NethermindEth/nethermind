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

    private static readonly FrozenDictionary<(string Fork, string Resource, string Method), int> s_forkVersionMap =
        new Dictionary<(string, string, string), int>
        {
            // newPayload (POST payloads)
            [(Paris, Payloads, "POST")] = 1,
            [(Shanghai, Payloads, "POST")] = 2,
            [(Cancun, Payloads, "POST")] = 3,
            [(Prague, Payloads, "POST")] = 4,
            [(Osaka, Payloads, "POST")] = 4,
            [(Amsterdam, Payloads, "POST")] = 5,

            // getPayload (GET payloads)
            [(Paris, Payloads, "GET")] = 1,
            [(Shanghai, Payloads, "GET")] = 2,
            [(Cancun, Payloads, "GET")] = 3,
            [(Prague, Payloads, "GET")] = 4,
            [(Osaka, Payloads, "GET")] = 5,
            [(Amsterdam, Payloads, "GET")] = 6,

            // forkchoiceUpdated (POST forkchoice)
            [(Paris, Forkchoice, "POST")] = 1,
            [(Shanghai, Forkchoice, "POST")] = 2,
            [(Cancun, Forkchoice, "POST")] = 3,
            [(Prague, Forkchoice, "POST")] = 3,
            [(Osaka, Forkchoice, "POST")] = 3,
            [(Amsterdam, Forkchoice, "POST")] = 4,

            // bodies/hash (POST)
            [(Paris, PayloadBodiesByHash, "POST")] = 1,
            [(Shanghai, PayloadBodiesByHash, "POST")] = 1,
            [(Cancun, PayloadBodiesByHash, "POST")] = 1,
            [(Prague, PayloadBodiesByHash, "POST")] = 1,
            [(Osaka, PayloadBodiesByHash, "POST")] = 1,
            [(Amsterdam, PayloadBodiesByHash, "POST")] = 2,

            // bodies (GET)
            [(Paris, PayloadBodiesByRange, "GET")] = 1,
            [(Shanghai, PayloadBodiesByRange, "GET")] = 1,
            [(Cancun, PayloadBodiesByRange, "GET")] = 1,
            [(Prague, PayloadBodiesByRange, "GET")] = 1,
            [(Osaka, PayloadBodiesByRange, "GET")] = 1,
            [(Amsterdam, PayloadBodiesByRange, "GET")] = 2,
        }.ToFrozenDictionary();

    public static int? MapForkToVersion(string fork, string resource, string httpMethod) =>
        s_forkVersionMap.TryGetValue((fork.ToLowerInvariant(), resource.ToLowerInvariant(), httpMethod), out int version)
            ? version
            : null;
}
