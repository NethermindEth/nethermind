// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexCompactRequest : IJsonRpcParam
{
    public bool Flush { get; set; }
    public int MergeIterations { get; set; }

    public void ReadJson(JsonElement json, JsonSerializerOptions options)
    {
        if (json.TryGetProperty("flush"u8, out JsonElement flushElement))
            Flush = flushElement.GetBoolean();

        if (json.TryGetProperty("MergeIterations"u8, out JsonElement mergeIterationsElement))
            MergeIterations = mergeIterationsElement.GetInt32();
    }
}
