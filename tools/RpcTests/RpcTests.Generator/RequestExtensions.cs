// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Generator;

public static class RequestExtensions
{
    extension(JsonNode json)
    {
        public string? GetId() => json["id"]?.ToString();
        public int? GetIntId() => json.GetId() is { } idStr && int.TryParse(idStr, out int id) ? id : null;
    }
}
