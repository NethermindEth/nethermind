// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

internal static class QueryParams
{
    /// <summary>
    /// Reads a required <see cref="ulong"/> query parameter named <paramref name="name"/> and
    /// validates it with <paramref name="isValid"/>. On failure, <paramref name="errorTask"/>
    /// is set to a pending <c>400 invalid-request</c> response that the caller MUST await.
    /// </summary>
    public static bool TryReadUlong(
        HttpContext ctx,
        string name,
        Func<ulong, bool> isValid,
        string validationDescription,
        out ulong value,
        [NotNullWhen(false)] out Task? errorTask)
    {
        if (ulong.TryParse(ctx.Request.Query[name], out value) && isValid(value))
        {
            errorTask = null;
            return true;
        }

        errorTask = SszEndpointHandlerBase.WriteErrorAsync(
            ctx,
            StatusCodes.Status400BadRequest,
            $"Missing or invalid '{name}' query parameter: {validationDescription}",
            SszRestErrorCodes.InvalidRequest);
        return false;
    }
}
