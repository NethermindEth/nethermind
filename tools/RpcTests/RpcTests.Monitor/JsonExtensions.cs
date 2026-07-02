// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

namespace Nethermind.RpcTests.Monitor;

internal static class JsonExtensions
{
    extension(JsonNode node)
    {
        public bool ReplaceAt(JsonPath path, JsonNode value)
        {
            JsonNode? target = path.Navigate(node);
            switch (target?.Parent)
            {
                case JsonObject obj:
                    obj[target.GetPropertyName()] = value;
                    return true;
                case JsonArray arr:
                    arr[target.GetElementIndex()] = value;
                    return true;
            }

            return false;
        }

        public bool RemoveAt(JsonPath path)
        {
            if (path.Navigate(node) is not { Parent: { } parent } old)
                return false;

            switch (parent)
            {
                case JsonObject obj: obj.Remove(old.GetPropertyName()); break;
                case JsonArray arr: arr.RemoveAt(old.GetElementIndex()); break;
            }

            return true;
        }
    }

    extension(JsonNode request)
    {
        public string MethodOrUnknown => request["method"]?.GetValue<string>() ?? "<unknown>";
    }
}
