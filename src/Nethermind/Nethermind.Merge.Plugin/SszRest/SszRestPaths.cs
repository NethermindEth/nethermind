// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Nethermind.Merge.Plugin.SszRest;

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

    public static readonly string PostV1Payloads = BuildCapability("POST", 1, Payloads, string.Empty);
    public static readonly string PostV2Payloads = BuildCapability("POST", 2, Payloads, string.Empty);
    public static readonly string PostV3Payloads = BuildCapability("POST", 3, Payloads, string.Empty);
    public static readonly string PostV4Payloads = BuildCapability("POST", 4, Payloads, string.Empty);
    public static readonly string PostV5Payloads = BuildCapability("POST", 5, Payloads, string.Empty);

    public static readonly string GetV1Payloads = BuildCapability("GET", 1, Payloads, "payload_id");
    public static readonly string GetV2Payloads = BuildCapability("GET", 2, Payloads, "payload_id");
    public static readonly string GetV3Payloads = BuildCapability("GET", 3, Payloads, "payload_id");
    public static readonly string GetV4Payloads = BuildCapability("GET", 4, Payloads, "payload_id");
    public static readonly string GetV5Payloads = BuildCapability("GET", 5, Payloads, "payload_id");
    public static readonly string GetV6Payloads = BuildCapability("GET", 6, Payloads, "payload_id");

    public static readonly string PostV1Forkchoice = BuildCapability("POST", 1, Forkchoice, string.Empty);
    public static readonly string PostV2Forkchoice = BuildCapability("POST", 2, Forkchoice, string.Empty);
    public static readonly string PostV3Forkchoice = BuildCapability("POST", 3, Forkchoice, string.Empty);
    public static readonly string PostV4Forkchoice = BuildCapability("POST", 4, Forkchoice, string.Empty);

    public static readonly string PostV1PayloadBodiesByHash = BuildCapability("POST", 1, PayloadBodiesByHash, string.Empty);
    public static readonly string PostV2PayloadBodiesByHash = BuildCapability("POST", 2, PayloadBodiesByHash, string.Empty);
    public static readonly string GetV1PayloadBodiesByRange = BuildCapability("GET", 1, PayloadBodiesByRange, string.Empty);
    public static readonly string GetV2PayloadBodiesByRange = BuildCapability("GET", 2, PayloadBodiesByRange, string.Empty);

    public static readonly string PostV1Blobs = BuildCapability("POST", 1, Blobs, string.Empty);
    public static readonly string PostV2Blobs = BuildCapability("POST", 2, Blobs, string.Empty);
    public static readonly string PostV3Blobs = BuildCapability("POST", 3, Blobs, string.Empty);
    public static readonly string PostV4Blobs = BuildCapability("POST", 4, Blobs, string.Empty);

    public static readonly string PostV1Capabilities = BuildCapability("GET", 1, Capabilities, string.Empty);
    public static readonly string PostV1ClientVersion = BuildCapability("GET", 1, ClientVersion, string.Empty);

    private static readonly FrozenDictionary<(string Fork, string Resource, string Method), int> s_forkVersionMap =
        new Dictionary<(string, string, string), int>
        {
            [(Paris, Payloads, "POST")] = 1,
            [(Shanghai, Payloads, "POST")] = 2,
            [(Cancun, Payloads, "POST")] = 3,
            [(Prague, Payloads, "POST")] = 4,
            [(Osaka, Payloads, "POST")] = 4,
            [(Amsterdam, Payloads, "POST")] = 5,

            [(Paris, Payloads, "GET")] = 1,
            [(Shanghai, Payloads, "GET")] = 2,
            [(Cancun, Payloads, "GET")] = 3,
            [(Prague, Payloads, "GET")] = 4,
            [(Osaka, Payloads, "GET")] = 5,
            [(Amsterdam, Payloads, "GET")] = 6,

            [(Paris, Forkchoice, "POST")] = 1,
            [(Shanghai, Forkchoice, "POST")] = 2,
            [(Cancun, Forkchoice, "POST")] = 3,
            [(Prague, Forkchoice, "POST")] = 3,
            [(Osaka, Forkchoice, "POST")] = 3,
            [(Amsterdam, Forkchoice, "POST")] = 4,

            [(Paris, PayloadBodiesByHash, "POST")] = 1,
            [(Shanghai, PayloadBodiesByHash, "POST")] = 1,
            [(Cancun, PayloadBodiesByHash, "POST")] = 1,
            [(Prague, PayloadBodiesByHash, "POST")] = 1,
            [(Osaka, PayloadBodiesByHash, "POST")] = 1,
            [(Amsterdam, PayloadBodiesByHash, "POST")] = 2,

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

    public static string BuildCapability(string httpMethod, int version, string resource, string extraPathName)
    {
        if (string.Equals(resource, Blobs, StringComparison.OrdinalIgnoreCase))
            return $"{httpMethod} /engine/{Blobs}/v{version}";

        if (string.Equals(resource, Capabilities, StringComparison.OrdinalIgnoreCase))
            return $"{httpMethod} /engine/{Capabilities}";

        if (string.Equals(resource, ClientVersion, StringComparison.OrdinalIgnoreCase))
            return $"{httpMethod} /engine/{ClientVersion}";

        string fork = GetCanonicalFork(httpMethod, version, resource);
        return extraPathName.Length != 0
            ? $"{httpMethod} /engine/{fork}/{resource}/{{{extraPathName}}}"
            : $"{httpMethod} /engine/{fork}/{resource}";
    }

    private static string GetCanonicalFork(string httpMethod, int version, string resource) =>
        (httpMethod, resource, version) switch
        {
            ("POST", Payloads, 1) => Paris,
            ("POST", Payloads, 2) => Shanghai,
            ("POST", Payloads, 3) => Cancun,
            ("POST", Payloads, 4) => Prague,
            ("POST", Payloads, 5) => Amsterdam,

            ("GET", Payloads, 1) => Paris,
            ("GET", Payloads, 2) => Shanghai,
            ("GET", Payloads, 3) => Cancun,
            ("GET", Payloads, 4) => Prague,
            ("GET", Payloads, 5) => Osaka,
            ("GET", Payloads, 6) => Amsterdam,

            ("POST", Forkchoice, 1) => Paris,
            ("POST", Forkchoice, 2) => Shanghai,
            ("POST", Forkchoice, 3) => Cancun,
            ("POST", Forkchoice, 4) => Amsterdam,

            ("POST", PayloadBodiesByHash, 1) => Shanghai,
            ("POST", PayloadBodiesByHash, 2) => Amsterdam,

            ("GET", PayloadBodiesByRange, 1) => Shanghai,
            ("GET", PayloadBodiesByRange, 2) => Amsterdam,

            _ => throw new InvalidOperationException(
                $"No SSZ REST canonical fork for {httpMethod} {resource} v{version}.")
        };
}
