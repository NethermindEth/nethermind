// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public record GethTraceOptions
{
    [Obsolete("Use EnableMemory instead.")]
    public bool DisableMemory { get => !EnableMemory; init => EnableMemory = !value; }

    public bool DisableStorage { get; init; }

    public bool EnableMemory { get; init; }

    public bool DisableStack { get; init; }

    public string Timeout { get; init; }

    public string Tracer { get; init; }

    public Hash256? TxHash { get; init; }

    public JsonElement? TracerConfig { get; init; }

    public Dictionary<Address, AccountOverride>? StateOverrides { get; init; }

    public BlockOverride? BlockOverrides { get; set; }

    public static GethTraceOptions Default { get; } = new();
}
