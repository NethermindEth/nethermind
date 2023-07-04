// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
