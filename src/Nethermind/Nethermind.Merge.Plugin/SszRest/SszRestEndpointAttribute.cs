// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

internal sealed class SszGetAttribute<TRequest, TResponse>(
    string resource,
    bool noStore = false)
    : SszRestAttribute("GET", resource, typeof(TRequest), typeof(TResponse), noStore)
    where TRequest : ISszRpcRequest<TRequest>
    where TResponse : ISszCodec<TResponse>
{
    protected override string GetExtraPathName(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length switch
        {
            0 => string.Empty,
            1 => ToSnakeCase(parameters[0].Name ?? throw new InvalidOperationException($"{method.Name} has an unnamed path parameter.")),
            _ => throw new InvalidOperationException($"{method.Name} has multiple parameters; SSZ REST currently supports one extra path segment.")
        };
    }
}

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
    bool noStore = false) : Attribute
{
    private const string EnginePrefix = "engine_";

    public string HttpMethod { get; } = httpMethod;
    public string Resource { get; } = resource;
    public Type RequestType { get; } = requestType;
    public Type ResponseType { get; } = responseType;
    public bool NoStore { get; } = noStore;

    internal SszRestEndpoint ToEndpoint(MethodInfo method)
    {
        if (!method.Name.StartsWith(EnginePrefix, StringComparison.Ordinal))
            throw Unsupported(method);

        int version = GetVersion(method.Name[EnginePrefix.Length..]);
        SszRestMetadata metadata = new(HttpMethod, version, Resource, GetExtraPathName(method), NoStore);
        return new SszRestEndpoint(method, metadata, RequestType, ResponseType);
    }

    protected virtual string GetExtraPathName(MethodInfo method) => string.Empty;

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

    protected static string ToSnakeCase(string value)
    {
        StringBuilder builder = new(value.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && value[i - 1] != '_' &&
                    (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                    builder.Append('_');

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

internal sealed class SszRestMetadata(
    string httpMethod,
    int version,
    string resource,
    string extraPathName = "",
    bool noStore = false)
{
    public string HttpMethod { get; } = httpMethod;
    public int Version { get; } = version;
    public string Resource { get; } = resource;
    public string ExtraPathName { get; } = extraPathName;
    public bool NoStore { get; } = noStore;
    public bool AcceptsPathExtra => ExtraPathName.Length != 0;

    public string Capability => AcceptsPathExtra
        ? $"{HttpMethod} /engine/v{Version}/{Resource}/{{{ExtraPathName}}}"
        : $"{HttpMethod} /engine/v{Version}/{Resource}";
}

internal readonly record struct SszRestEndpoint(
    MethodInfo Method,
    SszRestMetadata Metadata,
    Type RequestType,
    Type ResponseType);
