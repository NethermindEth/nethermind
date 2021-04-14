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

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public interface IJsonSerializer
    {
        T Deserialize<T>(Stream stream);
        T Deserialize<T>(string json);
        string Serialize<T>(T value, bool indented = false);
        long Serialize<T>(Stream stream, T value, bool indented = false);
        void RegisterConverter(JsonConverter converter);

        void RegisterConverters(IEnumerable<JsonConverter> converters)
        {
            foreach (JsonConverter converter in converters)
            {
                RegisterConverter(converter);
            }
        }
    }
}
