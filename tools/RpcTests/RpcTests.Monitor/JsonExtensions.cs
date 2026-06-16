// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Nethermind.RpcTests.Monitor;

internal static class JsonExtensions
{
    private static readonly JsonSerializerOptions _compactOptions = new()
    { WriteIndented = false, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static readonly JsonSerializerOptions _prettyOptions = new()
    { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    public static string ToCompactString(this JsonNode node) => node.ToJsonString(_compactOptions);
    public static string ToPrettyString(this JsonNode node) => node.ToJsonString(_prettyOptions);

    extension(JsonNode request)
    {
        public string MethodOrUnknown => request["method"]?.GetValue<string>() ?? "<unknown>";
    }
}
