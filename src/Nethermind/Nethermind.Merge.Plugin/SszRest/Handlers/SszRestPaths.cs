// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public static class SszRestPaths
{
    public const string Payloads = "payloads";

    public const string Forkchoice = "forkchoice";

    public const string Capabilities = "capabilities";

    public const string ClientVersion = "identity";

    public const string PayloadBodiesByHash = "bodies/hash";

    public const string PayloadBodiesByRange = "bodies";

    public const string Blobs = "blobs";

    public const string PostV1Payloads = "POST /engine/v2/paris/payloads";
    public const string GetV1Payloads = "GET /engine/v2/paris/payloads/{payload_id}";
    public const string PostV1Forkchoice = "POST /engine/v2/paris/forkchoice";
    public const string PostV1Capabilities = "GET /engine/v2/capabilities";
    public const string PostV1ClientVersion = "GET /engine/v2/identity";

    // Shanghai
    public const string PostV2Payloads = "POST /engine/v2/shanghai/payloads";
    public const string PostV2Forkchoice = "POST /engine/v2/shanghai/forkchoice";
    public const string GetV2Payloads = "GET /engine/v2/shanghai/payloads/{payload_id}";
    public const string PostV1PayloadBodiesByHash = "POST /engine/v2/shanghai/bodies/hash";
    public const string GetV1PayloadBodiesByRange = "GET /engine/v2/shanghai/bodies";

    // Cancun
    public const string PostV3Payloads = "POST /engine/v2/cancun/payloads";
    public const string PostV3Forkchoice = "POST /engine/v2/cancun/forkchoice";
    public const string GetV3Payloads = "GET /engine/v2/cancun/payloads/{payload_id}";
    public const string PostV1Blobs = "POST /engine/v2/blobs/v1";

    // Prague
    public const string PostV4Payloads = "POST /engine/v2/prague/payloads";
    public const string GetV4Payloads = "GET /engine/v2/prague/payloads/{payload_id}";

    // Osaka
    public const string GetV5Payloads = "GET /engine/v2/osaka/payloads/{payload_id}";
    public const string PostV2Blobs = "POST /engine/v2/blobs/v2";
    public const string PostV3Blobs = "POST /engine/v2/blobs/v3";

    // Amsterdam
    public const string PostV5Payloads = "POST /engine/v2/amsterdam/payloads";
    public const string GetV6Payloads = "GET /engine/v2/amsterdam/payloads/{payload_id}";
    public const string PostV4Forkchoice = "POST /engine/v2/amsterdam/forkchoice";
    public const string PostV2PayloadBodiesByHash = "POST /engine/v2/amsterdam/bodies/hash";
    public const string GetV2PayloadBodiesByRange = "GET /engine/v2/amsterdam/bodies";
    public const string PostV4Blobs = "POST /engine/v2/blobs/v4";
}
