// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;

namespace RpcTestsMon;

internal static class JsonExtensions
{
    private static readonly JsonSerializerOptions _compactOptions = new() { WriteIndented = false };

    public static string ToCompactString(this JsonNode node) => node.ToJsonString(_compactOptions);

    extension(JsonNode request)
    {
        public string MethodOrUnknown => request["method"]?.GetValue<string>() ?? "<unknown>";
    }
}
