// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.Core.Collections;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>A repeatable query parameter, with comma-joined values split the same way multiple <c>key=</c> occurrences would be.</summary>
internal readonly record struct QueryValues(IReadOnlyList<string> Values)
{
    /// <summary>Binds the query parameter named after <paramref name="parameter"/>.</summary>
    /// <remarks>The values are pooled and returned once the response completes, so a streamed result may still read them.</remarks>
    public static ValueTask<QueryValues> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        StringValues raws = context.Request.Query[parameter.Name!];
        ArrayPoolList<string> values = [with(raws.Count)];
        context.Response.RegisterForDispose(values);
        foreach (string? raw in raws)
        {
            foreach (Range range in raw.AsSpan().Split(','))
            {
                ReadOnlySpan<char> part = raw.AsSpan()[range].Trim();
                if (!part.IsEmpty) values.Add(part.ToString());
            }
        }

        return new(new QueryValues(values));
    }
}
