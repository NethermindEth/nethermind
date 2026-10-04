// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public record GethTraceOptions
{
    // Setters rather than init: source-generated metadata assigns every init-only member, defaulting absent ones, which would
    // let one of these aliases reset the other; setters apply only the members present, in JSON order.
    [Obsolete("Use EnableMemory instead.")]
    public bool DisableMemory { get => !EnableMemory; set => EnableMemory = !value; }

    public bool DisableStorage { get; init; }

    public bool EnableMemory { get; set; }

    public bool EnableReturnData { get; init; }

    public bool DisableStack { get; init; }

    [JsonConverter(typeof(CustomTimeDurationConverter))]
    public TimeSpan? Timeout { get; init; }

    public string Tracer { get; init; }

    public Hash256? TxHash { get; init; }

    public JsonElement? TracerConfig { get; init; }

    public Dictionary<Address, AccountOverride>? StateOverrides { get; init; }

    public BlockOverride? BlockOverrides { get; set; }

    [JsonIgnore]
    public bool NoBaseFee { get; init; }

    /// <summary>
    /// Where the callTracer's <c>withLog</c> indexes start and how they carry across transactions.
    /// </summary>
    /// <remarks>
    /// Set by the tracing entry points, never by the caller: one instance shared across a sequential block replay,
    /// an independent receipt-seeded instance per transaction otherwise, and <c>null</c>, numbering from zero,
    /// for a synthetic call.
    /// </remarks>
    [JsonIgnore]
    public BlockLogIndex? LogIndex { get; init; }

    /// <summary>
    /// When set, overrides <c>JsonRpc.EnableTracingStreamMode</c> for this single call.
    /// </summary>
    public bool? StreamMode { get; init; }

    public static GethTraceOptions Default => new();
}
