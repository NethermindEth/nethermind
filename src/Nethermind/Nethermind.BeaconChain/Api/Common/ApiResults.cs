// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Http;
using Nethermind.BeaconChain.Spec;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>The results endpoint handlers return, each deferring to the shared response writers.</summary>
internal static class ApiResults
{
    public static IResult Error(int statusCode, string message) =>
        Write(c => ApiErrors.Write(c, statusCode, message, c.RequestAborted));

    public static IResult Data<T>(T data) =>
        Write(c => BeaconApiJson.WriteDataAsync(c, data, c.RequestAborted));

    /// <summary>A JSON body that carries no beacon-api envelope.</summary>
    public static IResult Json<T>(T body) =>
        Write(c => BeaconApiJson.WriteAsync(c, body, c.RequestAborted));

    public static IResult Envelope<T>(T data, bool executionOptimistic, bool finalized) =>
        Write(c => BeaconApiJson.WriteEnvelopeAsync(c, data, executionOptimistic, finalized, c.RequestAborted));

    /// <inheritdoc cref="BeaconApiJson.WriteVersionedEnvelopeAsync"/>
    public static IResult VersionedEnvelope(string version, bool executionOptimistic, bool finalized, Func<BeaconJsonStream, Task> writeData) =>
        Write(c => BeaconApiJson.WriteVersionedEnvelopeAsync(c, version, executionOptimistic, finalized, writeData));

    /// <inheritdoc cref="BeaconApiJson.WriteStreamedEnvelopeAsync"/>
    public static IResult StreamedEnvelope(bool executionOptimistic, bool finalized, Func<BeaconJsonStream, Task> writeData) =>
        Write(c => BeaconApiJson.WriteStreamedEnvelopeAsync(c, executionOptimistic, finalized, writeData));

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

    private sealed class WriteResult(Func<HttpContext, Task> write) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => write(httpContext);
    }
}
