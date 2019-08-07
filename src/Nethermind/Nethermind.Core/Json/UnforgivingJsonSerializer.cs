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

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.Core.Json
{
    public class UnforgivingJsonSerializer : IJsonSerializer
    {
        public T DeserializeAnonymousType<T>(string json, T definition)
        {
            return JsonConvert.DeserializeAnonymousType(json, definition);
        }

        public T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json);
        }

        public (T Model, List<T> Collection) DeserializeObjectOrArray<T>(string json)
        {
            var token = JToken.Parse(json);
            if (token is JArray array)
            {
                foreach (var tokenElement in array)
                {
                    UpdateParams(tokenElement);
                }

                return (default, array.ToObject<List<T>>());
            }
            UpdateParams(token);

            return (token.ToObject<T>(), null);
        }
        
        private void UpdateParams(JToken token)
        {
            var paramsToken = token.SelectToken("params");
            if (paramsToken == null)
            {
                paramsToken = token.SelectToken("Params");
                if (paramsToken == null)
                {
                    return;
                }

//                if (paramsToken == null)
//                {
//                    throw new FormatException("Missing 'params' token");
//                }
            }
            
            var values = new List<string>();
            foreach (var value in paramsToken.Value<IEnumerable<object>>())
            {
                var valueString = value?.ToString();
                if (valueString == null)
                {
                    values.Add($"\"null\"");
                    continue;
                }
                
                if (valueString.StartsWith("{") || valueString.StartsWith("["))
                {
                    values.Add(Serialize(valueString));
                    continue;
                }
                values.Add($"\"{valueString}\"");
            }

            var json = $"[{string.Join(",", values)}]";
            paramsToken.Replace(JToken.Parse(json));
        }

        public string Serialize<T>(T value, bool indented = false)
        {
            return JsonConvert.SerializeObject(value, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = indented ? Formatting.Indented : Formatting.None
            });
        }

        public void RegisterConverter(JsonConverter converter)
        {
            throw new NotImplementedException();
        }
    }
}