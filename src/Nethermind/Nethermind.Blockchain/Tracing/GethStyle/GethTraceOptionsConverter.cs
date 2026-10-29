// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal sealed class GethTraceOptionsConverter : JsonConverter<GethTraceOptions>
{
    public override GethTraceOptions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<WireOptions>(ref reader, options)!.Materialize();

    public override void Write(Utf8JsonWriter writer, GethTraceOptions value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new WireOptions(value), options);

    /// <remarks>Inheriting init-only properties adds synthetic constructor arguments even when setter shadows hide them.</remarks>
    internal sealed class WireOptions
    {
        public WireOptions() { }

        public WireOptions(GethTraceOptions options)
        {
            _disableStorage = options.DisableStorage;
            _enableReturnData = options.EnableReturnData;
            _disableStack = options.DisableStack;
            _limit = options.Limit;
            Timeout = options.ParsedTimeout;
            _enableMemory = options.EnableMemory;
            Tracer = options.Tracer;
            TxHash = options.TxHash;
            TxIndex = options.TxIndex;
            TracerConfig = options.TracerConfig;
            StateOverrides = options.StateOverrides;
            BlockOverrides = options.BlockOverrides;
            NoBaseFee = options.NoBaseFee;
            LogIndex = options.LogIndex;
            StreamMode = options.StreamMode;
        }

        private bool _disableStorage;
        private bool _enableReturnData;
        private bool _disableStack;
        private long _limit;
        private bool _enableMemory;

        public bool? DisableStorage { get => _disableStorage; set { if (value is { } v) _disableStorage = v; } }
        public bool? EnableReturnData { get => _enableReturnData; set { if (value is { } v) _enableReturnData = v; } }
        public bool? DisableStack { get => _disableStack; set { if (value is { } v) _disableStack = v; } }

        [JsonConverter(typeof(GethTraceOptions.LimitConverter))]
        public long? Limit { get => _limit; set { if (value is { } v) _limit = v; } }

        [JsonConverter(typeof(ParsedDurationConverter))]
        public ParsedTraceDuration Timeout { get; set; }

        public bool? DisableMemory { get => !_enableMemory; set { if (value is { } v) _enableMemory = !v; } }
        public bool? EnableMemory { get => _enableMemory; set { if (value is { } v) _enableMemory = v; } }
        public string Tracer { get; set; }
        public Hash256? TxHash { get; set; }

        [JsonConverter(typeof(GethTraceOptions.TransactionIndexConverter))]
        public ulong? TxIndex { get; set; }

        public JsonElement? TracerConfig { get; set; }
        public Dictionary<Address, AccountOverride>? StateOverrides { get; set; }
        public BlockOverride? BlockOverrides { get; set; }

        [JsonIgnore]
        public bool NoBaseFee { get; set; }

        [JsonIgnore]
        public BlockLogIndex? LogIndex { get; set; }

        public bool? StreamMode { get; set; }

        internal GethTraceOptions Materialize() => new()
        {
            DisableStorage = _disableStorage,
            EnableReturnData = _enableReturnData,
            DisableStack = _disableStack,
            Limit = _limit,
            ParsedTimeout = Timeout,
            EnableMemory = _enableMemory,
            Tracer = Tracer,
            TxHash = TxHash,
            TxIndex = TxIndex,
            TracerConfig = TracerConfig,
            StateOverrides = StateOverrides,
            BlockOverrides = BlockOverrides,
            NoBaseFee = NoBaseFee,
            LogIndex = LogIndex,
            StreamMode = StreamMode
        };
    }

    internal sealed class ParsedDurationConverter : JsonConverter<ParsedTraceDuration>
    {
        public override ParsedTraceDuration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            CustomTimeDurationConverter.ReadDuration(ref reader);

        public override void Write(Utf8JsonWriter writer, ParsedTraceDuration value, JsonSerializerOptions options)
        {
            if (value.RawText is { } rawText) writer.WriteStringValue(rawText);
            else if (value.Value is { } duration) CustomTimeDurationConverter.WriteFormatted(writer, duration);
            else writer.WriteNullValue();
        }
    }
}
