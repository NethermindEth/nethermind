// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>Shared JSON options and envelope writers for the Beacon API. Every DTO names its own
/// wire field with <see cref="JsonPropertyNameAttribute"/> rather than relying on a naming policy,
/// so a field's on-wire name is never a guess.</summary>
internal static class BeaconApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Task WriteDataAsync<T>(HttpContext ctx, T data, CancellationToken token = default)
    {
        ctx.Response.ContentType = ContentNegotiation.Json;
        return JsonSerializer.SerializeAsync(ctx.Response.Body, new DataEnvelope<T>(data), Options, token);
    }

    public static Task WriteEnvelopeAsync<T>(HttpContext ctx, T data, bool executionOptimistic, bool finalized, CancellationToken token = default)
    {
        ctx.Response.ContentType = ContentNegotiation.Json;
        return JsonSerializer.SerializeAsync(ctx.Response.Body, new FullEnvelope<T>(executionOptimistic, finalized, data), Options, token);
    }

    /// <summary>
    /// Streams the fork-versioned envelope (<c>version</c>, <c>execution_optimistic</c>,
    /// <c>finalized</c>, <c>data</c>) the v2 block and debug-state endpoints return, with
    /// <paramref name="writeData"/> producing the <c>data</c> value through the shared writer.
    /// </summary>
    public static async Task WriteVersionedEnvelopeAsync(HttpContext ctx, string version, bool executionOptimistic, bool finalized, Func<BeaconJsonStream, Task> writeData)
    {
        ctx.Response.ContentType = ContentNegotiation.Json;
        await using BeaconJsonStream stream = new(ctx.Response.BodyWriter, ctx.RequestAborted);
        Utf8JsonWriter w = stream.Writer;
        w.WriteStartObject();
        w.WriteString("version", version);
        w.WriteBoolean("execution_optimistic", executionOptimistic);
        w.WriteBoolean("finalized", finalized);
        w.WritePropertyName("data");
        await writeData(stream);
        w.WriteEndObject();
        await stream.FlushAsync();
    }

    private sealed record DataEnvelope<T>([property: JsonPropertyName("data")] T Data);

    private sealed record FullEnvelope<T>(
        [property: JsonPropertyName("execution_optimistic")] bool ExecutionOptimistic,
        [property: JsonPropertyName("finalized")] bool Finalized,
        [property: JsonPropertyName("data")] T Data);
}
