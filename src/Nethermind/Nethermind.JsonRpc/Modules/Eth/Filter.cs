/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.JsonRpc.Data;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class Filter : IJsonRpcRequest
    {
        public BlockParameter FromBlock { get; set; }
        public BlockParameter ToBlock { get; set; }
        public object Address { get; set; }
        public IEnumerable<object> Topics { get; set; }

        private readonly IJsonSerializer _jsonSerializer;

        public Filter()
        {
            _jsonSerializer = new UnforgivingJsonSerializer();
        }

        public Filter(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
        }

        public void FromJson(string jsonValue)
        {
            var filter = _jsonSerializer.Deserialize<JObject>(jsonValue);
            FromBlock = GetBlockParameter(filter["fromBlock"]?.ToObject<string>());
            ToBlock = GetBlockParameter(filter["toBlock"]?.ToObject<string>());
            Address = GetAddress(filter["address"]);
            Topics = GetTopics(filter["topics"] as JArray);
        }

        private static BlockParameter GetBlockParameter(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new BlockParameter
                {
                    Type = BlockParameterType.Latest
                };
            }

            var block = new BlockParameter();
            block.FromJson(value);

            return block;
        }

        private static object GetAddress(JToken token) => GetSingleOrMany(token);

        private static IEnumerable<object> GetTopics(JArray array)
        {
            if (array is null)
            {
                yield break;
            }

            foreach (var token in array)
            {
                yield return GetSingleOrMany(token);
            }
        }

        private static object GetSingleOrMany(JToken token)
        {
            switch (token)
            {
                case null:
                    return null;
                case JArray _:
                    return token.ToObject<IEnumerable<string>>();
                default:
                    return token.ToObject<string>();
            }
        }
    }
}