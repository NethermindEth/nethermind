// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>A repeatable query parameter, with comma-joined values split the same way multiple <c>key=</c> occurrences would be.</summary>
internal readonly record struct QueryValues(List<string> Values)
{
    /// <summary>Binds the query parameter named after <paramref name="parameter"/>.</summary>
    public static ValueTask<QueryValues> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        List<string> values = [];
        foreach (string? raw in context.Request.Query[parameter.Name!])
        {
            if (raw is not null) values.AddRange(raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return new(new QueryValues(values));
    }
}
