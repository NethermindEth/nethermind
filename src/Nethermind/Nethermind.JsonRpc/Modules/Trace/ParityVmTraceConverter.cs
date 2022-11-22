// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.ParityStyle;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityVmTraceConverter : JsonConverter<ParityVmTrace>
    {
        public override void WriteJson(JsonWriter writer, ParityVmTrace value, JsonSerializer serializer)
        {
            writer.WriteStartObject();

            writer.WriteProperty("code", value.Code ?? Array.Empty<byte>(), serializer);
            writer.WriteProperty("ops", value.Operations, serializer);

            writer.WriteEndObject();
        }

        public override ParityVmTrace ReadJson(JsonReader reader, Type objectType, ParityVmTrace existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
