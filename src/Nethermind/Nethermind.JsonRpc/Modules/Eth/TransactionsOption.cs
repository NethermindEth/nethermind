// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

namespace Nethermind.JsonRpc.Modules.Eth;

public class TransactionsOption : IJsonRpcParam
{
    public bool IncludeTransactions { get; set; }

    public void ReadJson(JsonElement jsonValue, JsonSerializerOptions options)
    {
        if (jsonValue.ValueKind == JsonValueKind.True)
        {
            IncludeTransactions = true;
            return;
        }
        if (jsonValue.ValueKind == JsonValueKind.False)
        {
            IncludeTransactions = false;
            return;
        }
        JsonElement value;
        if (jsonValue.ValueKind == JsonValueKind.Object)
        {
            if (jsonValue.TryGetProperty("includeTransactions"u8, out value))
            {
                if (value.ValueKind == JsonValueKind.True)
                {
                    IncludeTransactions = true;
                    return;
                }
                if (value.ValueKind == JsonValueKind.False)
                {
                    IncludeTransactions = false;
                    return;
                }
            }
            return;
        }

        string? text = jsonValue.GetString();
        bool isTrue = string.Equals(text, bool.TrueString, StringComparison.InvariantCultureIgnoreCase);
        bool isFalse = string.Equals(text, bool.FalseString, StringComparison.InvariantCultureIgnoreCase);

        IncludeTransactions = isTrue || isFalse
            ? isTrue
            : GetIncludeTransactions(jsonValue.TryGetProperty("includeTransactions"u8, out value) ? value : null, options);
    }

    private static bool GetIncludeTransactions(JsonElement? token, JsonSerializerOptions options) => token switch
    {
        null => false,
        _ => token.GetValueOrDefault().Deserialize<bool>(options),
    };
}
