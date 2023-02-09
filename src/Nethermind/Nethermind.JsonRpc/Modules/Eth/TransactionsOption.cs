// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Modules.Eth;

public class TransactionsOption : IJsonRpcParam
{
    public bool IncludeTransactions { get; set; }

    public void ReadJson(JsonSerializer serializer, string jsonValue)
    {
        JObject jObject = serializer.Deserialize<JObject>(jsonValue.ToJsonTextReader());
        IncludeTransactions = GetIncludeTransactions(jObject["includeTransactions"]);
    }

    private static bool GetIncludeTransactions(JToken? token) => token switch
    {
        null => false,
        _ => token.ToObject<bool>(),
    };
}
