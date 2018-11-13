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
using Nethermind.Core.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.Core
{
    public class JsonSerializer : IJsonSerializer
    {
        private readonly ILogger _logger;

        public JsonSerializer(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
        }

        public T DeserializeAnonymousType<T>(string json, T definition)
        {
            try
            {
                return JsonConvert.DeserializeAnonymousType(json, definition);
            }
            catch (Exception e)
            {
                _logger.Error("Error during json deserialization", e);
                return default(T);
            }
        }

        public T Deserialize<T>(string json)
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception e)
            {
                _logger.Error("Error during json deserialization", e);
                return default(T);
            }           
        }

        public (T Model, IEnumerable<T> Collection) DeserializeObjectOrArray<T>(string json)
        {
            try
            {
                var token = JToken.Parse(json);
                if (token is JArray)
                {
                    foreach (var tokenElement in (JArray)token)
                    {
                        UpdateParams(tokenElement);
                    }

                    return (default, token.ToObject<List<T>>());
                }
                UpdateParams(token);

                return (token.ToObject<T>(), null);
            }
            catch (Exception e)
            {
                _logger.Error("Error during json deserialization", e);
                return (default, null);
            }
        }

        private void UpdateParams(JToken token)
        {
            var paramsToken = token.SelectToken("params");
            if (paramsToken == null)
            {
                paramsToken = token.SelectToken("Params");
                if (paramsToken == null)
                {
                    throw new FormatException("Missing 'params' token");
                }
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
            try
            {
                return JsonConvert.SerializeObject(value, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    Formatting = indented ? Formatting.Indented : Formatting.None
                });
            }
            catch (Exception e)
            {
                _logger.Error("Error during json serialization", e);
                return null;
            }          
        }
    }
}