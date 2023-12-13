// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.JsonRpc.Modules.Trace;

namespace Nethermind.Evm.Tracing.ParityStyle;

[JsonConverter(typeof(ParityVmTraceConverter))]
public class ParityVmTrace
{
    public byte[] Code { get; set; }
    public ParityVmOperationTrace[] Operations { get; set; }
}
