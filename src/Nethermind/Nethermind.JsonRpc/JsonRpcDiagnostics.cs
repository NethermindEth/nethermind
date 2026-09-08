// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

/// <summary>
/// The optional recording and tracing side of request processing: the <c>RpcRecorder</c> files and the Trace-level
/// response dumps.
/// </summary>
/// <remarks>
/// Every member is a no-op or a pass-through unless the corresponding diagnostic is switched on, so callers may
/// invoke them unconditionally on the hot path. The recorder exists only for some values of
/// <see cref="IJsonRpcConfig.RpcRecorderState"/>, so both <c>IsRecording</c> predicates test for it rather than
/// inferring it from the flags.
/// </remarks>
internal sealed class JsonRpcDiagnostics
{
    private static readonly StreamPipeReaderOptions PipeReaderOptions = new(leaveOpen: false);

    private readonly IJsonRpcConfig _jsonRpcConfig;
    private readonly ILogger _logger;
    private readonly Recorder? _recorder;

    public JsonRpcDiagnostics(IJsonRpcConfig jsonRpcConfig, IFileSystem fileSystem, ILogger logger)
    {
        _jsonRpcConfig = jsonRpcConfig;
        _logger = logger;

        if (jsonRpcConfig.RpcRecorderState != RpcRecorderState.None)
        {
            if (_logger.IsWarn) _logger.Warn("Enabling JSON RPC diagnostics recorder - this will affect performance and should be only used in a diagnostics mode.");
            string recorderBaseFilePath = jsonRpcConfig.RpcRecorderBaseFilePath.GetApplicationResourcePath();
            _recorder = new Recorder(recorderBaseFilePath, fileSystem, _logger);
        }
    }

    public bool IsRecordingRequest => _recorder is not null && (_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Request) != 0;

    private bool IsRecordingResponse => _recorder is not null && (_jsonRpcConfig.RpcRecorderState & RpcRecorderState.Response) != 0;

    public JsonRpcResult.Entry RecordResponse(JsonRpcResponse response, in RpcReport report) =>
        RecordResponse(new JsonRpcResult.Entry(response, report));

    public JsonRpcResult.Entry RecordResponse(in JsonRpcResult.Entry result) =>
        !IsRecordingResponse ? result : RecordResponseSlow(result);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void RecordRequest(ReadOnlyMemory<byte> requestBody) =>
        _recorder!.RecordRequest(Encoding.UTF8.GetString(requestBody.Span));

    /// <summary>Records the whole request body, returning a reader positioned back at its start.</summary>
    /// <remarks>The original reader is drained, so the caller must continue with the returned one.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public async ValueTask<PipeReader> RecordRequest(PipeReader reader)
    {
        Stream memoryStream = RecyclableStream.GetStream("recorder");
        await reader.CopyToAsync(memoryStream);
        memoryStream.Seek(0, SeekOrigin.Begin);

        StreamReader streamReader = new(memoryStream);

        string requestString = await streamReader.ReadToEndAsync();
        _recorder!.RecordRequest(requestString);

        memoryStream.Seek(0, SeekOrigin.Begin);
        return PipeReader.Create(memoryStream, PipeReaderOptions);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void TraceResult(in JsonRpcResult.Entry response) =>
        _logger.Trace($"Sending JSON RPC response: {SerializeForDiagnostics(response)}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void TraceResult(JsonRpcErrorResponse response) =>
        _logger.Trace($"Sending JSON RPC response: {SerializeResponseForDiagnostics(response)}");

    public static string SerializeResponseForDiagnostics(JsonRpcResponse response)
    {
        ArrayBufferWriter<byte> writer = new();
        JsonRpcResponseWriter.Write(writer, response, EthereumJsonSerializer.JsonOptionsIndented);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsonRpcResult.Entry RecordResponseSlow(in JsonRpcResult.Entry result)
    {
        _recorder!.RecordResponse(SerializeForDiagnostics(result));
        return result;
    }

    private static string SerializeForDiagnostics(in JsonRpcResult.Entry response)
    {
        JsonRpcResponse responseToSerialize = TryGetDiagnosticResponse(response.Response, out JsonRpcResponse? diagnosticResponse)
            ? diagnosticResponse
            : response.Response;

        ArrayBufferWriter<byte> writer = new();
        JsonRpcResponseWriter.Write(writer, responseToSerialize, EthereumJsonSerializer.JsonOptionsIndented);
        using JsonDocument document = JsonDocument.Parse(writer.WrittenMemory);
        return JsonSerializer.Serialize(new DiagnosticJsonRpcResult(document.RootElement, response.Report), EthereumJsonSerializer.JsonOptionsIndented);
    }

    /// <summary>Replaces a streamed result with a placeholder, since diagnostics must not consume the stream.</summary>
    private static bool TryGetDiagnosticResponse(JsonRpcResponse response, [NotNullWhen(true)] out JsonRpcResponse? diagnosticResponse)
    {
        diagnosticResponse = response switch
        {
            _ when response.TryGetStreamableResult(out _) => new JsonRpcSuccessResponse
            {
                Id = response.Id,
                Result = "# streamable response omitted #"
            },
            JsonRpcErrorResponse { Error.Data: IStreamableResult } errorResponse => new JsonRpcErrorResponse
            {
                Id = errorResponse.Id,
                Error = new Error
                {
                    Code = errorResponse.Error.Code,
                    Message = errorResponse.Error.Message,
                    Data = "# streamable error data omitted #",
                    SuppressWarning = errorResponse.Error.SuppressWarning
                }
            },
            _ => null
        };

        return diagnosticResponse is not null;
    }

    private readonly record struct DiagnosticJsonRpcResult(JsonElement Response, RpcReport Report);
}
