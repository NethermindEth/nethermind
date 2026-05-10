// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Reflection;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

internal sealed class SszGetAttribute<TRequest, TResponse>()
    : SszRestAttribute("GET", typeof(TRequest), typeof(TResponse))
    where TRequest : ISszRpcRequest<TRequest>
    where TResponse : ISszCodec<TResponse>;

internal sealed class SszPostAttribute<TRequest, TResponse>()
    : SszRestAttribute("POST", typeof(TRequest), typeof(TResponse))
    where TRequest : ISszRpcRequest<TRequest>
    where TResponse : ISszCodec<TResponse>;

[AttributeUsage(AttributeTargets.Method)]
internal abstract class SszRestAttribute(string httpMethod, Type requestType, Type responseType) : Attribute
{
    private const string EnginePrefix = "engine_";
    private const string PayloadIdPathExtraName = "payload_id";

    public string HttpMethod { get; } = httpMethod;
    public Type RequestType { get; } = requestType;
    public Type ResponseType { get; } = responseType;

    internal SszRestMetadata ToMetadata(MethodInfo method)
    {
        SszRestMetadata metadata = Infer(method, RequestType, ResponseType);
        if (!StringComparer.Ordinal.Equals(HttpMethod, metadata.HttpMethod))
            throw new InvalidOperationException($"{method.Name} is an SSZ REST {metadata.HttpMethod} endpoint, but is marked as {HttpMethod}.");

        return metadata;
    }

    private static SszRestMetadata Infer(MethodInfo method, Type requestType, Type responseType)
    {
        if (!method.Name.StartsWith(EnginePrefix, StringComparison.Ordinal))
            throw Unsupported(method);

        (string family, int version) = SplitVersion(method.Name[EnginePrefix.Length..]);

        return family switch
        {
            "exchangeCapabilities" => new("POST", version, SszRestPaths.Capabilities, requestType, responseType),
            "forkchoiceUpdated" => new("POST", version, SszRestPaths.Forkchoice, requestType, responseType),
            "getBlobs" => new("POST", version, SszRestPaths.Blobs, requestType, responseType),
            "getClientVersion" => new("POST", version, SszRestPaths.ClientVersion, requestType, responseType),
            "getPayload" => new("GET", version, SszRestPaths.Payloads, requestType, responseType, true, PayloadIdPathExtraName, true),
            "getPayloadBodiesByHash" => new("POST", version, SszRestPaths.PayloadBodiesByHash, requestType, responseType),
            "getPayloadBodiesByRange" => new("POST", version, SszRestPaths.PayloadBodiesByRange, requestType, responseType),
            "newPayload" => new("POST", version, SszRestPaths.Payloads, requestType, responseType),
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

    private static InvalidOperationException Unsupported(MethodInfo method) =>
        new($"No SSZ REST convention is registered for {method.Name}.");
}

internal sealed class SszRestMetadata(
    string httpMethod,
    int version,
    string resource,
    Type requestType,
    Type responseType,
    bool acceptsPathExtra = false,
    string extraPathName = "",
    bool noStore = false)
{
    public string HttpMethod { get; } = httpMethod;
    public int Version { get; } = version;
    public string Resource { get; } = resource;
    public Type RequestType { get; } = requestType;
    public Type ResponseType { get; } = responseType;
    public bool AcceptsPathExtra { get; } = acceptsPathExtra;
    public string ExtraPathName { get; } = extraPathName;
    public bool NoStore { get; } = noStore;

    public string Capability => AcceptsPathExtra
        ? $"{HttpMethod} /engine/v{Version}/{Resource}/{{{ExtraPathName}}}"
        : $"{HttpMethod} /engine/v{Version}/{Resource}";
}
