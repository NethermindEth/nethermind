// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;

namespace Nethermind.LightClient.Rpc;

/// <summary>Stages bounded HTTP and WebSocket responses using the shared JSON-RPC writer.</summary>
internal sealed class RpcResponseBuffer(int limit, string limitMessage,
    Action<JsonRpcResponse, RpcReport>? report = null, bool batchInput = false) : IJsonRpcResponseSink, IDisposable
{
    private readonly BoundedJsonBuffer _output = new(limit, 6 * limit);
    private BoundedJsonBuffer? _finalError;
    private Action<bool>? _completePolls;
    private int _responses;
    private bool _batch;
    private bool _sealed;

    internal ReadOnlyMemory<byte> WrittenMemory => _output.WrittenMemory;
    internal bool HasResponses => _responses != 0;
    internal int? ErrorCode { get; private set; }
    public long BytesWritten => _output.WrittenMemory.Length;
    public bool StopRequested { get; private set; }

    internal void CommitPolls() => FinishPolls(accepted: true);

    private void FinishPolls(bool accepted)
    {
        Action<bool>? complete = _completePolls;
        _completePolls = null;
        complete?.Invoke(accepted);
    }

    public JsonRpcErrorResponse? GetBatchLimitResponse(JsonRpcRequest? request)
    {
        using BoundedJsonBuffer error = new(limit, 6 * limit);
        try
        {
            JsonRpcResponseWriter.Write(error, Error(request?.Id ?? JsonRpcId.Null), EthereumJsonSerializer.JsonOptions);
            return (long)_output.WrittenMemory.Length + error.WrittenMemory.Length + FinalError.Length + 3 > limit
                ? Error(JsonRpcId.Null) : null;
        }
        catch (RpcException) { return Error(JsonRpcId.Null); }
    }

    private ReadOnlySpan<byte> FinalError
    {
        get
        {
            if (_finalError is null)
            {
                _finalError = new(limit);
                JsonRpcResponseWriter.Write(_finalError, Error(JsonRpcId.Null), EthereumJsonSerializer.JsonOptions);
            }
            return _finalError.WrittenMemory.Span;
        }
    }

    public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport rpcReport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (batchInput && response is JsonRpcErrorResponse { Error.Code: ErrorCodes.InvalidRequest })
            RpcServiceDiagnostics.SuppressOutcome(response);
        Write(response, rpcReport);
        return ValueTask.CompletedTask;
    }

    public ValueTask BeginBatchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _batch = true;
        Append("["u8);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport rpcReport, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(response, rpcReport);
        return ValueTask.CompletedTask;
    }

    public ValueTask EndBatchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Append("]"u8);
        return ValueTask.CompletedTask;
    }

    private void Write(JsonRpcResponse response, RpcReport rpcReport)
    {
        if (_sealed) return;
        if (!_batch) ErrorCode = response is JsonRpcErrorResponse { Error: { } error } ? error.Code : null;
        int separator = _batch && _responses != 0 ? 1 : 0;
        int reserve = _batch ? FinalError.Length + 2 : 0;
        int remaining = limit - _output.WrittenMemory.Length - separator - reserve;
        if (remaining <= 0)
        {
            Seal(FinalError);
            RpcServiceDiagnostics.OverrideOutcome(response, "error -32005");
            return;
        }
        int itemLimit = remaining;
        if (rpcReport.Success && remaining > BoundedJson.ResultLimit)
        {
            using BoundedJsonBuffer envelope = new(limit, 6 * limit);
            JsonRpcResponseWriter.Write(envelope, new JsonRpcSuccessResponse { Id = response.Id }, EthereumJsonSerializer.JsonOptions);
            itemLimit = Math.Min(remaining, BoundedJson.ResultLimit + envelope.WrittenMemory.Length - "null"u8.Length);
        }
        bool payloadLimited = itemLimit < remaining;
        using BoundedJsonBuffer item = new(itemLimit, 6 * limit);
        try
        {
            JsonRpcResponseWriter.Write(item, response, EthereumJsonSerializer.JsonOptions);
            if (separator != 0) Append(","u8);
            Append(item.WrittenMemory.Span);
            _responses++;
        }
        catch (RpcException)
        {
            RpcServiceDiagnostics.OverrideOutcome(response, "error -32005");
            using BoundedJsonBuffer error = new(Math.Max(1, limit - _output.WrittenMemory.Length - separator - reserve), 6 * limit);
            try
            {
                JsonRpcResponseWriter.Write(error, Error(response.Id, payloadLimited ? BoundedJson.TooLarge().Message : null),
                    EthereumJsonSerializer.JsonOptions);
            }
            catch (RpcException)
            {
                Seal(FinalError);
                return;
            }
            if (separator != 0) Append(","u8);
            Append(error.WrittenMemory.Span);
            _responses++;
            StopRequested |= !payloadLimited;
            if (!_batch) ErrorCode = ErrorCodes.LimitExceeded;
            return;
        }
        if (response is RpcFilters.PollResponse poll) _completePolls += poll.TakeCompletion();
        report?.Invoke(response, rpcReport);
    }

    private JsonRpcErrorResponse Error(JsonRpcId id, string? message = null) => new()
    {
        Id = id,
        Error = new Error { Code = ErrorCodes.LimitExceeded, Message = message ?? limitMessage },
    };

    private void Seal(ReadOnlySpan<byte> finalError)
    {
        if (_batch && _responses != 0) Append(","u8);
        Append(finalError);
        _responses++;
        StopRequested = true;
        _sealed = true;
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_output.GetSpan(bytes.Length));
        _output.Advance(bytes.Length);
    }

    public void Dispose()
    {
        FinishPolls(accepted: false);
        _output.Dispose();
        _finalError?.Dispose();
    }
}
