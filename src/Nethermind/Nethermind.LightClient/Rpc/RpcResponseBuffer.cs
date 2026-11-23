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
        int finalErrorLength = FinalError.Length;
        long withRequestId = finalErrorLength - "null"u8.Length + (long)(request?.Id ?? JsonRpcId.Null).GetEncodedLength();
        return _output.WrittenMemory.Length + withRequestId + finalErrorLength + 3 > limit ? Error(JsonRpcId.Null) : null;
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
        int position = _output.WrittenMemory.Length;
        int separator = _batch && _responses != 0 ? 1 : 0;
        int reserve = _batch ? FinalError.Length + 2 : 0;
        int remaining = limit - position - separator - reserve;
        if (remaining <= 0)
        {
            Seal(FinalError);
            RpcServiceDiagnostics.OverrideOutcome(response, "error -32005");
            return;
        }
        int itemLimit = remaining;
        if (rpcReport.Success && remaining > BoundedJson.ResultLimit)
            itemLimit = Math.Min(remaining, BoundedJson.ResultLimit + JsonRpcResponseWriter.GetCompactSuccessEnvelopeLength(response.Id));
        bool payloadLimited = itemLimit < remaining;
        try
        {
            WriteItem(response, itemLimit);
        }
        catch (RpcException)
        {
            RpcServiceDiagnostics.OverrideOutcome(response, "error -32005");
            try
            {
                WriteItem(Error(response.Id, payloadLimited ? BoundedJson.TooLarge().Message : null), remaining);
            }
            catch (RpcException)
            {
                Seal(FinalError);
                return;
            }
            StopRequested |= !payloadLimited;
            if (!_batch) ErrorCode = ErrorCodes.LimitExceeded;
            return;
        }
        if (response is RpcFilters.PollResponse poll) _completePolls += poll.TakeCompletion();
        report?.Invoke(response, rpcReport);

        void WriteItem(JsonRpcResponse item, int budget)
        {
            _output.WriteLimit = position + separator + budget;
            try
            {
                if (separator != 0) Append(","u8);
                JsonRpcResponseWriter.Write(_output, item, EthereumJsonSerializer.JsonOptions);
                _responses++;
            }
            catch
            {
                _output.Rewind(position);
                throw;
            }
            finally { _output.WriteLimit = limit; }
        }
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
        if (!_batch) ErrorCode = ErrorCodes.LimitExceeded;
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
