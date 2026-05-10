// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Reflection;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

internal sealed class SszGetAttribute<TRequest, TResponse>(
    string resource,
    string extraPathName = "",
    bool noStore = false)
    : SszRestAttribute("GET", resource, typeof(TRequest), typeof(TResponse), extraPathName.Length != 0, extraPathName, noStore)
    where TRequest : ISszRpcRequest<TRequest>
    where TResponse : ISszCodec<TResponse>;

internal sealed class SszPostAttribute<TRequest, TResponse>(string resource)
    : SszRestAttribute("POST", resource, typeof(TRequest), typeof(TResponse))
    where TRequest : ISszRpcRequest<TRequest>
    where TResponse : ISszCodec<TResponse>;

[AttributeUsage(AttributeTargets.Method)]
internal abstract class SszRestAttribute(
    string httpMethod,
    string resource,
    Type requestType,
    Type responseType,
    bool acceptsPathExtra = false,
    string extraPathName = "",
    bool noStore = false) : Attribute
{
    private const string EnginePrefix = "engine_";

    public string HttpMethod { get; } = httpMethod;
    public string Resource { get; } = resource;
    public Type RequestType { get; } = requestType;
    public Type ResponseType { get; } = responseType;
    public bool AcceptsPathExtra { get; } = acceptsPathExtra;
    public string ExtraPathName { get; } = extraPathName;
    public bool NoStore { get; } = noStore;

    internal SszRestMetadata ToMetadata(MethodInfo method)
    {
        if (!method.Name.StartsWith(EnginePrefix, StringComparison.Ordinal))
            throw Unsupported(method);

        int version = GetVersion(method.Name[EnginePrefix.Length..]);
        return new SszRestMetadata(HttpMethod, version, Resource, RequestType, ResponseType, AcceptsPathExtra, ExtraPathName, NoStore);
    }

    private static int GetVersion(string name)
    {
        int versionStart = name.Length - 1;
        while (versionStart >= 0 && char.IsDigit(name[versionStart]))
            versionStart--;

        if (versionStart >= 0 && versionStart < name.Length - 1 && name[versionStart] == 'V')
            return int.Parse(name[(versionStart + 1)..], CultureInfo.InvariantCulture);

        return 1;
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
