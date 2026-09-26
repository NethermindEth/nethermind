// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Generator;

internal static class JsonExtensions
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    public static string ToCompactString(this JsonNode node) => node.ToJsonString(CompactJsonOptions);
}
