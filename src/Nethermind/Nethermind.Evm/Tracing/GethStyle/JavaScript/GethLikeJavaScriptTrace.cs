// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;

using Nethermind.Evm.Tracing.GethStyle.JavaScript;

namespace Nethermind.Evm.Tracing.GethStyle;

[JsonConverter(typeof(GethLikeJavaScriptTraceConverter))]
public class GethLikeJavaScriptTrace
{
    private static readonly object _empty = new { };
    public object Value { get; set; } = _empty;

    public override string ToString()
    {
        return Value.ToString() ?? string.Empty;
    }
}
