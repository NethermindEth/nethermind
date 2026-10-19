// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;

public class NativeCallTracerConfig
{
    private static readonly NativeCallTracerConfig DefaultConfig = new();

    public bool OnlyTopCall { get; init; }

    public bool WithLog { get; init; }

    internal static NativeCallTracerConfig FromJson(JsonElement? json, JsonSerializerOptions options)
    {
        JsonConfig? config = TypeInfoJsonSerializer.Deserialize<JsonConfig>(json, options);
        return config is null ? DefaultConfig : new()
        {
            OnlyTopCall = config.OnlyTopCall ?? false,
            WithLog = config.WithLog ?? false
        };
    }

    // encoding/json leaves scalar booleans unchanged on null, including after a duplicate key.
    internal sealed class JsonConfig
    {
        public bool? OnlyTopCall { get; set => field = value ?? field; } = false;

        public bool? WithLog { get; set => field = value ?? field; } = false;
    }
}
