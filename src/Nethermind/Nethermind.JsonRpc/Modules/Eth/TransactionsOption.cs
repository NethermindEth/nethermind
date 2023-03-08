// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Modules.Eth;

public class TransactionsOption : IJsonRpcParam
{
    public bool IncludeTransactions { get; set; }

    public void ReadJson(JsonSerializer serializer, string jsonValue)
    {
        bool isTrue = string.Equals(jsonValue, bool.TrueString, StringComparison.InvariantCultureIgnoreCase);
        bool isFalse = string.Equals(jsonValue, bool.FalseString, StringComparison.InvariantCultureIgnoreCase);

        IncludeTransactions = isTrue || isFalse
            ? isTrue
            : GetIncludeTransactions(serializer.Deserialize<JObject>(jsonValue.ToJsonTextReader())["includeTransactions"]);
    }

    private static bool GetIncludeTransactions(JToken? token) => token switch
    {
        null => false,
        _ => token.ToObject<bool>(),
    };
}
