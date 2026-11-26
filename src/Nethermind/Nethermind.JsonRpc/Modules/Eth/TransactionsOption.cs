// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core.Exceptions;

namespace Nethermind.JsonRpc.Modules.Eth;

public class TransactionsOption : IJsonRpcParam
{
    public bool IncludeTransactions { get; set; }

    /// <remarks>
    /// Accepts a boolean, the string <c>"true"</c> or <c>"false"</c> in any case, or an object, whose
    /// <c>includeTransactions</c> sets the option when it is a boolean and leaves the option as it is otherwise.
    /// </remarks>
    /// <exception cref="SafePublicMessageFormatException">The value is of any other kind, or another string.</exception>
    public void ReadJson(JsonElement jsonValue, JsonSerializerOptions options)
    {
        switch (jsonValue.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                IncludeTransactions = jsonValue.GetBoolean();
                return;
            case JsonValueKind.Object:
                if (jsonValue.TryGetProperty("includeTransactions"u8, out JsonElement value)
                    && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    IncludeTransactions = value.GetBoolean();
                }
                return;
            case JsonValueKind.String:
                string? text = jsonValue.GetString();
                bool isTrue = string.Equals(text, bool.TrueString, StringComparison.OrdinalIgnoreCase);
                if (isTrue || string.Equals(text, bool.FalseString, StringComparison.OrdinalIgnoreCase))
                {
                    IncludeTransactions = isTrue;
                    return;
                }
                break;
        }

        throw new SafePublicMessageFormatException("expected a boolean or an object with includeTransactions");
    }
}
