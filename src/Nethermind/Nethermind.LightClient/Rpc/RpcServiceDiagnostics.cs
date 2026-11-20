// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.JsonRpc;

namespace Nethermind.LightClient.Rpc;

/// <summary>Observes shared RPC dispatch through response completion, including notifications.</summary>
internal sealed class RpcServiceDiagnostics(IJsonRpcService service, ILogger logger, LightClientMetrics? metrics) : IJsonRpcService
{
    private static readonly ConditionalWeakTable<JsonRpcResponse, Observation> Pending = [];

    internal static void OverrideOutcome(JsonRpcResponse response, string outcome)
    {
        if (Pending.TryGetValue(response, out Observation? observation)) observation.Outcome = outcome;
    }

    internal static void SuppressOutcome(JsonRpcResponse response)
    {
        if (Pending.TryGetValue(response, out Observation? observation)) observation.Suppressed = true;
    }

    public async ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            JsonRpcResponse response = await service.SendRequestAsync(request, context, cancellationToken);
            string outcome = response switch
            {
                JsonRpcErrorResponse { Error: { } error } => $"error {error.Code}",
                IResultWrapper wrapper when wrapper.ErrorCode != 0 => $"error {wrapper.ErrorCode}",
                _ => request.Id.IsMissing ? "notification" : "ok",
            };
            Observation observation = new(this, request.Method, outcome, started,
                context.RequestAborted.CanBeCanceled ? context.RequestAborted : cancellationToken);
            Pending.Add(response, observation);
            response.AddDisposable(() =>
            {
                Pending.Remove(response);
                observation.Complete();
            });
            return response;
        }
        catch
        {
            Record(request.Method, cancellationToken.IsCancellationRequested || context.RequestAborted.IsCancellationRequested
                ? "cancelled" : "error -32000", started);
            throw;
        }
    }

    private void Record(string? method, string outcome, long started, bool log = true)
    {
        method ??= "<invalid>";
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        metrics?.RecordRpc(method, outcome, elapsed.TotalSeconds);
        if (!log) return;
        string display = method.Length <= 64 ? method : method[..64] + "…";
        display = string.Create(display.Length, display, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++) destination[i] = char.IsControl(source[i]) ? ' ' : source[i];
        });
        logger.LogInformation("RPC {Method} -> {Outcome} in {ElapsedMs:F1} ms", display, outcome, elapsed.TotalMilliseconds);
    }

    public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, in JsonRpcId id, string? methodName = null)
    {
        JsonRpcErrorResponse response = service.GetErrorResponse(errorCode, errorMessage, in id, methodName);
        if (errorCode == ErrorCodes.InvalidRequest)
        {
            Observation observation = new(this, "<invalid>", $"error {errorCode}", Stopwatch.GetTimestamp(), default, log: false);
            Pending.Add(response, observation);
            response.AddDisposable(() =>
            {
                Pending.Remove(response);
                observation.Complete();
            });
        }
        return response;
    }

    public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, string? methodName = null) =>
        GetErrorResponse(errorCode, errorMessage, in JsonRpcId.Null, methodName);

    private sealed class Observation(RpcServiceDiagnostics owner, string method, string outcome, long started,
        CancellationToken cancellationToken, bool log = true)
    {
        private int _completed;
        internal string Outcome { get; set; } = outcome;
        internal bool Suppressed { get; set; }

        internal void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0 && !Suppressed)
                owner.Record(method, cancellationToken.IsCancellationRequested ? "cancelled" : Outcome, started, log);
        }
    }
}
