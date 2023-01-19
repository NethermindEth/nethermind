// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Utils
{
    public static class JTokenUtils
    {
        public static IEnumerable<JToken> ParseMulticontent(TextReader jsonReader)
        {
            using JsonReader reader = new JsonTextReader(jsonReader) { SupportMultipleContent = true };
            while (reader.Read())
            {
                yield return JToken.Load(reader);
            }
        }
    }
}
