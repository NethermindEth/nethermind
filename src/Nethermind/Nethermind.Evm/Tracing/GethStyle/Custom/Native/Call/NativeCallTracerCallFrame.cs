// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Call;

[JsonConverter(typeof(NativeCallTracerCallFrameConverter))]
public class NativeCallTracerCallFrame
{
    public Instruction Type { get; set; }

    public Address? From { get; set; }

    public long Gas { get; set; }

    public long GasUsed { get; set; }

    public Address? To { get; set; }

    public byte[]? Input { get; set; }

    public byte[]? Output { get; set; }

    public string? Error { get; set; }

    public string? RevertReason { get; set; }

    public List<NativeCallTracerCallFrame> Calls { get; set; }

    public List<NativeCallTracerLogEntry>? Logs { get; set; }

    public UInt256? Value { get; set; }

    public bool RevertedSnapshot { get; set; }
}
