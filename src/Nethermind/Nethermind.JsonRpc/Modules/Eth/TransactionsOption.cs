//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Modules.Eth;

public class TransactionsOption : IJsonRpcParam
{
    public bool IncludeTransactions { get; set; }
        
    public void FromJson(JsonSerializer serializer, string jsonValue)
    {
        JObject jObject = serializer.Deserialize<JObject>(jsonValue.ToJsonTextReader());
        IncludeTransactions = GetIncludeTransactions(jObject["includeTransactions"]);
    }
    
    private static bool GetIncludeTransactions(JToken? token)
    {
        switch (token)
        {
            case null:
                return false;
            default:
                return token.ToObject<bool>();
        }
    }
}
