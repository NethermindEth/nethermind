// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace RpcTestsMon;

internal static class ResponseComparer
{
    public static bool Compare(JsonNode target, JsonNode reference) =>
        JsonNode.DeepEquals(target, reference);
}
