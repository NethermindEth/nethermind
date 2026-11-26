// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>The results endpoint handlers return.</summary>
internal static class ApiResults
{
    public static IResult Error(int statusCode, string message) =>
        Write(c => ApiErrors.Write(c, statusCode, message, c.RequestAborted));

    /// <summary>A JSON body that carries no beacon-api envelope.</summary>
    public static IResult Json<T>(T body) => Write(c =>
    {
        c.Response.ContentType = ContentNegotiation.Json;
        return JsonSerializer.SerializeAsync(c.Response.Body, body, BeaconApiJson.Options, c.RequestAborted);
    });

    public static IResult Data<T>(T data) => Json(new DataEnvelope<T>(data));

    public static IResult Envelope<T>(T data, bool executionOptimistic, bool finalized) =>
        Json(new FullEnvelope<T>(executionOptimistic, finalized, data));

    /// <summary>
    /// Streams the fork-versioned envelope (<c>version</c>, <c>execution_optimistic</c>,
    /// <c>finalized</c>, <c>data</c>) the v2 block and debug-state endpoints return, with
    /// <paramref name="writeData"/> producing the <c>data</c> value through the shared writer.
    /// </summary>
    public static IResult VersionedEnvelope(string version, bool executionOptimistic, bool finalized, Func<BeaconJsonStream, Task> writeData) =>
        Write(c => WriteEnvelopeAsync(c, version, executionOptimistic, finalized, writeData));

    /// <summary>Streams the unversioned <c>execution_optimistic</c>, <c>finalized</c>, <c>data</c> envelope with <paramref name="writeData"/> producing the <c>data</c> value, so a large list is never buffered whole.</summary>
    public static IResult StreamedEnvelope(bool executionOptimistic, bool finalized, Func<BeaconJsonStream, Task> writeData) =>
        Write(c => WriteEnvelopeAsync(c, null, executionOptimistic, finalized, writeData));

    public static IResult Ssz(byte[] ssz) => Write(c =>
    {
        c.Response.ContentType = ContentNegotiation.OctetStream;
        return c.Response.Body.WriteAsync(ssz, c.RequestAborted).AsTask();
    });

    /// <summary>Builds and writes only the representation the request's <c>Accept</c> header selects.</summary>
    public static IResult SszOrJson(Func<IResult> ssz, Func<IResult> json) =>
        Write(c => (ContentNegotiation.Negotiate(c, sszSupported: true) == ContentNegotiation.ResponseFormat.Ssz ? ssz() : json()).ExecuteAsync(c));

    public static IResult Write(Func<HttpContext, Task> write) => new WriteResult(write);

    public static IResult WithConsensusVersion(this IResult result, BeaconChainSpec spec, ulong slot) =>
        result.WithConsensusVersion(spec.ForkAtEpoch(spec.GetEpoch(slot)));

    /// <summary>Adds the <c>Eth-Consensus-Version</c> header to <paramref name="result"/>.</summary>
    public static IResult WithConsensusVersion(this IResult result, BeaconFork fork) => Write(c =>
    {
        c.Response.Headers[ResponseEnvelope.ConsensusVersionHeader] = ResponseEnvelope.ForkName(fork);
        return result.ExecuteAsync(c);
    });

    private static async Task WriteEnvelopeAsync(HttpContext c, string? version, bool executionOptimistic, bool finalized, Func<BeaconJsonStream, Task> writeData)
    {
        c.Response.ContentType = ContentNegotiation.Json;
        await using BeaconJsonStream stream = new(c.Response.BodyWriter, c.RequestAborted);
        Utf8JsonWriter w = stream.Writer;
        w.WriteStartObject();
        if (version is not null) w.WriteString("version", version);
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

    private sealed class WriteResult(Func<HttpContext, Task> write) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => write(httpContext);
    }
}
