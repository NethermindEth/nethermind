// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle.Custom;

[JsonConverter(typeof(GethLikeCustomTraceConverter))]
public class GethLikeCustomTrace
{
    private static readonly object _empty = new { };
    public object Value { get; set; } = _empty;

    public override string ToString()
    {
        return Value.ToString() ?? string.Empty;
    }
}
