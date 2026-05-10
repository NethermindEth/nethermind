// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Reflection;
using Nethermind.Merge.Plugin;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Marks an Engine API method as exposed through an SSZ REST GET endpoint.
/// </summary>
public sealed class SszGetAttribute() : SszRestAttribute("GET");

/// <summary>
/// Marks an Engine API method as exposed through an SSZ REST POST endpoint.
/// </summary>
public sealed class SszPostAttribute() : SszRestAttribute("POST");

/// <summary>
/// Base attribute for SSZ REST Engine API endpoint markers.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public abstract class SszRestAttribute(string httpMethod) : Attribute
{
    private const string EnginePrefix = "engine_";
    private const string PayloadIdPathExtraName = "payload_id";

    public string HttpMethod { get; } = httpMethod;

    internal SszRestMetadata ToMetadata(MethodInfo method)
    {
        SszRestMetadata metadata = Infer(method);
        if (!StringComparer.Ordinal.Equals(HttpMethod, metadata.HttpMethod))
            throw new InvalidOperationException($"{method.Name} is an SSZ REST {metadata.HttpMethod} endpoint, but is marked as {HttpMethod}.");

        return metadata;
    }

    private static SszRestMetadata Infer(MethodInfo method)
    {
        if (!method.Name.StartsWith(EnginePrefix, StringComparison.Ordinal))
            throw Unsupported(method);

        (string family, int version) = SplitVersion(method.Name[EnginePrefix.Length..]);

        return family switch
        {
            "exchangeCapabilities" => new("POST", version, SszRestPaths.Capabilities,
                SszRestRequest.Capabilities, SszRestResponse.Capabilities),
            "forkchoiceUpdated" => new("POST", version, SszRestPaths.Forkchoice,
                ForkchoiceUpdatedRequest(version), SszRestResponse.ForkchoiceUpdated),
            "getBlobs" => new("POST", version, SszRestPaths.Blobs,
                SszRestRequest.GetBlobs, GetBlobsResponse(version)),
            "getClientVersion" => new("POST", version, SszRestPaths.ClientVersion,
                SszRestRequest.ClientVersion, SszRestResponse.ClientVersion),
            "getPayload" => new("GET", version, SszRestPaths.Payloads,
                SszRestRequest.PayloadId, GetPayloadResponse(version), true, PayloadIdPathExtraName, true),
            "getPayloadBodiesByHash" => new("POST", version, SszRestPaths.PayloadBodiesByHash,
                SszRestRequest.PayloadBodiesByHash, PayloadBodiesResponse(version)),
            "getPayloadBodiesByRange" => new("POST", version, SszRestPaths.PayloadBodiesByRange,
                SszRestRequest.PayloadBodiesByRange, PayloadBodiesResponse(version)),
            "newPayload" => new("POST", version, SszRestPaths.Payloads,
                NewPayloadRequest(version), SszRestResponse.PayloadStatus),
            _ => throw Unsupported(method)
        };
    }

    private static (string Family, int Version) SplitVersion(string name)
    {
        int versionStart = name.Length - 1;
        while (versionStart >= 0 && char.IsDigit(name[versionStart]))
            versionStart--;

        if (versionStart >= 0 && versionStart < name.Length - 1 && name[versionStart] == 'V')
            return (name[..versionStart], int.Parse(name[(versionStart + 1)..], CultureInfo.InvariantCulture));

        return (name, 1);
    }

    private static SszRestRequest ForkchoiceUpdatedRequest(int version) => version switch
    {
        1 => SszRestRequest.ForkchoiceUpdatedV1,
        2 => SszRestRequest.ForkchoiceUpdatedV2,
        3 => SszRestRequest.ForkchoiceUpdatedV3,
        4 => SszRestRequest.ForkchoiceUpdatedV4,
        _ => throw UnsupportedVersion(nameof(IEngineRpcModule.engine_forkchoiceUpdatedV1), version)
    };

    private static SszRestRequest NewPayloadRequest(int version) => version switch
    {
        1 => SszRestRequest.NewPayloadV1,
        2 => SszRestRequest.NewPayloadV2,
        3 => SszRestRequest.NewPayloadV3,
        4 => SszRestRequest.NewPayloadV4,
        5 => SszRestRequest.NewPayloadV5,
        _ => throw UnsupportedVersion(nameof(IEngineRpcModule.engine_newPayloadV1), version)
    };

    private static SszRestResponse GetBlobsResponse(int version) => version switch
    {
        1 => SszRestResponse.GetBlobsV1,
        2 => SszRestResponse.GetBlobsV2,
        3 => SszRestResponse.GetBlobsV3,
        _ => throw UnsupportedVersion(nameof(IEngineRpcModule.engine_getBlobsV1), version)
    };

    private static SszRestResponse GetPayloadResponse(int version) => version switch
    {
        1 => SszRestResponse.GetPayloadV1,
        2 => SszRestResponse.GetPayloadV2,
        3 => SszRestResponse.GetPayloadV3,
        4 => SszRestResponse.GetPayloadV4,
        5 => SszRestResponse.GetPayloadV5,
        6 => SszRestResponse.GetPayloadV6,
        _ => throw UnsupportedVersion(nameof(IEngineRpcModule.engine_getPayloadV1), version)
    };

    private static SszRestResponse PayloadBodiesResponse(int version) => version switch
    {
        1 => SszRestResponse.PayloadBodiesV1,
        2 => SszRestResponse.PayloadBodiesV2,
        _ => throw UnsupportedVersion(nameof(IEngineRpcModule.engine_getPayloadBodiesByHashV1), version)
    };

    private static InvalidOperationException Unsupported(MethodInfo method) =>
        new($"No SSZ REST convention is registered for {method.Name}.");

    private static InvalidOperationException UnsupportedVersion(string methodFamily, int version) =>
        new($"No SSZ REST convention is registered for {methodFamily} version {version}.");
}

public sealed class SszRestMetadata(
    string httpMethod,
    int version,
    string resource,
    SszRestRequest request,
    SszRestResponse response,
    bool acceptsPathExtra = false,
    string extraPathName = "",
    bool noStore = false)
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
