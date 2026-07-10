// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

[JsonConverter(typeof(ParityVmTraceConverter))]
public class ParityVmTrace
{
    public byte[] Code { get; set; }
    public IReadOnlyList<ParityVmOperationTrace> Operations { get; set; }
}
