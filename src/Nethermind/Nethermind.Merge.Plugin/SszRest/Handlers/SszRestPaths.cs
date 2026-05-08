// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public static class SszRestPaths
{
    public const string Payloads = "payloads";

    public const string Forkchoice = "forkchoice";

    public const string Capabilities = "capabilities";

    public const string ClientVersion = "client/version";

    public const string TransitionConfiguration = "transition-configuration";

    public const string PayloadBodiesByHash = "payloads/bodies/by-hash";

    public const string PayloadBodiesByRange = "payloads/bodies/by-range";

    public const string Blobs = "blobs";

    public const string PostV1Payloads = "POST /engine/v1/payloads";
    public const string GetV1Payloads = "GET /engine/v1/payloads/{payload_id}";
    public const string PostV1Forkchoice = "POST /engine/v1/forkchoice";
    public const string PostV1Capabilities = "POST /engine/v1/capabilities";
    public const string PostV1ClientVersion = "POST /engine/v1/client/version";
    public const string PostV1TransitionConfig = "POST /engine/v1/transition-configuration";

    // Shanghai
    public const string PostV2Payloads = "POST /engine/v2/payloads";
    public const string PostV2Forkchoice = "POST /engine/v2/forkchoice";
    public const string GetV2Payloads = "GET /engine/v2/payloads/{payload_id}";
    public const string PostV1PayloadBodiesByHash = "POST /engine/v1/payloads/bodies/by-hash";
    public const string PostV1PayloadBodiesByRange = "POST /engine/v1/payloads/bodies/by-range";

    // Cancun
    public const string PostV3Payloads = "POST /engine/v3/payloads";
    public const string PostV3Forkchoice = "POST /engine/v3/forkchoice";
    public const string GetV3Payloads = "GET /engine/v3/payloads/{payload_id}";
    public const string PostV1Blobs = "POST /engine/v1/blobs";

    // Prague
    public const string PostV4Payloads = "POST /engine/v4/payloads";
    public const string GetV4Payloads = "GET /engine/v4/payloads/{payload_id}";

    // Osaka
    public const string GetV5Payloads = "GET /engine/v5/payloads/{payload_id}";
    public const string PostV2Blobs = "POST /engine/v2/blobs";
    public const string PostV3Blobs = "POST /engine/v3/blobs";

    // Amsterdam
    public const string PostV5Payloads = "POST /engine/v5/payloads";
    public const string GetV6Payloads = "GET /engine/v6/payloads/{payload_id}";
    public const string PostV4Forkchoice = "POST /engine/v4/forkchoice";
    public const string PostV2PayloadBodiesByHash = "POST /engine/v2/payloads/bodies/by-hash";
    public const string PostV2PayloadBodiesByRange = "POST /engine/v2/payloads/bodies/by-range";
}
