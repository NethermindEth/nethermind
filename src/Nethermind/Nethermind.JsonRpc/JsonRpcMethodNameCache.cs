// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.JsonRpc;

public static class JsonRpcMethodNameCache
{
    public static string? Intern(JsonElement methodElement) =>
        GeneratedRpcMethodNames.Intern(methodElement);
}
