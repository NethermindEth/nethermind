// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Writes the beacon-api error envelope: <c>{"code": ..., "message": ...}</c>.</summary>
internal static class ApiErrors
{
    public static Task Write(HttpContext ctx, int statusCode, string message, CancellationToken token = default)
    {
        if (ctx.Response.HasStarted)
        {
            // A handler already wrote (partial) success bytes; there is no clean way to turn those
            // into an error body without corrupting the stream, so give up rather than send garbage.
            return Task.CompletedTask;
        }

        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = ContentNegotiation.Json;
        return JsonSerializer.SerializeAsync(ctx.Response.Body, new ErrorDto(statusCode, message), BeaconApiJson.Options, token);
    }

    private sealed record ErrorDto(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message);
}
