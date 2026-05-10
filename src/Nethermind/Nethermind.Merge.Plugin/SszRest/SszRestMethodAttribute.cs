// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Merge.Plugin.SszRest;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SszRestMethodAttribute(
    string httpMethod,
    int version,
    string resource,
    SszRestRequest request,
    SszRestResponse response,
    bool acceptsPathExtra = false,
    string extraPathName = "",
    bool noStore = false) : Attribute
{
    public string HttpMethod { get; } = httpMethod;
    public int Version { get; } = version;
    public string Resource { get; } = resource;
    public SszRestRequest Request { get; } = request;
    public SszRestResponse Response { get; } = response;
    public bool AcceptsPathExtra { get; } = acceptsPathExtra;
    public string ExtraPathName { get; } = extraPathName;
    public bool NoStore { get; } = noStore;

    public string Capability => AcceptsPathExtra
        ? $"{HttpMethod} /engine/v{Version}/{Resource}/{{{ExtraPathName}}}"
        : $"{HttpMethod} /engine/v{Version}/{Resource}";
}

public enum SszRestRequest
{
    Capabilities,
    ClientVersion,
    ForkchoiceUpdatedV1,
    ForkchoiceUpdatedV2,
    ForkchoiceUpdatedV3,
    ForkchoiceUpdatedV4,
    GetBlobs,
    PayloadBodiesByHash,
    PayloadBodiesByRange,
    PayloadId,
    NewPayloadV1,
    NewPayloadV2,
    NewPayloadV3,
    NewPayloadV4,
    NewPayloadV5,
}

public enum SszRestResponse
{
    Capabilities,
    ClientVersion,
    ForkchoiceUpdated,
    GetBlobsV1,
    GetBlobsV2,
    GetBlobsV3,
    GetPayloadV1,
    GetPayloadV2,
    GetPayloadV3,
    GetPayloadV4,
    GetPayloadV5,
    GetPayloadV6,
    PayloadBodiesV1,
    PayloadBodiesV2,
    PayloadStatus,
}
