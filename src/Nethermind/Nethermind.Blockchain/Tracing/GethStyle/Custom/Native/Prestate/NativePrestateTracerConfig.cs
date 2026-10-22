// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;

public class NativePrestateTracerConfig
{
    public bool DiffMode { get; init; }

    /// <summary>Gets whether to include initially empty accounts. Defaults to false; cannot be combined with diff mode.</summary>
    public bool IncludeEmpty { get; init; }

    /// <summary>Gets whether to omit contract code bytes. Defaults to false; code hashes remain included.</summary>
    public bool DisableCode { get; init; }

    /// <summary>Gets whether to omit storage from prestate and state diffs. Defaults to false.</summary>
    public bool DisableStorage { get; init; }

    internal static NativePrestateTracerConfig FromJson(JsonElement? json, JsonSerializerOptions options)
    {
        JsonConfig? config = GethLikeNativeTracerFactory.ParseConfig<JsonConfig>(json, options);
        return new()
        {
            DiffMode = config?.DiffMode ?? false,
            IncludeEmpty = config?.IncludeEmpty ?? false,
            DisableCode = config?.DisableCode ?? false,
            DisableStorage = config?.DisableStorage ?? false
        };
    }

    // encoding/json leaves scalar booleans unchanged on null, including after a duplicate key.
    internal sealed class JsonConfig
    {
        public bool? DiffMode { get; set => field = value ?? field; } = false;

        public bool? IncludeEmpty { get; set => field = value ?? field; } = false;

        public bool? DisableCode { get; set => field = value ?? field; } = false;

        public bool? DisableStorage { get; set => field = value ?? field; } = false;
    }
}
