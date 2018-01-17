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
using Nevermind.Core;
using Newtonsoft.Json;

namespace Nevermind.Json
{
    public class JsonSerializer : IJsonSerializer
    {
        private readonly ILogger _logger;

        public JsonSerializer(ILogger logger)
        {
            _logger = logger;
        }

        public T DeserializeAnonymousType<T>(string json, T definition)
        {
            try
            {
                return JsonConvert.DeserializeAnonymousType<T>(json, definition);
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

        public string Serialize<T>(T value)
        {
            try
            {
                return JsonConvert.SerializeObject(value, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
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