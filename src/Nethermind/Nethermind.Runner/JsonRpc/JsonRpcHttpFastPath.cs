// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Nethermind.Core.Authentication;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Runner.JsonRpc;

internal sealed class JsonRpcHttpFastPath(
    IJsonRpcUrlCollection jsonRpcUrlCollection,
    JsonRpcProcessor jsonRpcProcessor,
    JsonRpcService jsonRpcService,
    IJsonRpcConfig jsonRpcConfig,
    IJsonRpcLocalStats jsonRpcLocalStats,
    IRpcAuthentication? rpcAuthentication,
    ILogManager logManager)
{
    private static ReadOnlySpan<byte> JsonOpeningBracket => [(byte)'['];
    private static ReadOnlySpan<byte> JsonComma => [(byte)','];
    private static ReadOnlySpan<byte> JsonClosingBracket => [(byte)']'];

    private readonly IJsonRpcUrlCollection _jsonRpcUrlCollection = jsonRpcUrlCollection;
    private readonly JsonRpcProcessor _jsonRpcProcessor = jsonRpcProcessor;
    private readonly JsonRpcService _jsonRpcService = jsonRpcService;
    private readonly IJsonRpcConfig _jsonRpcConfig = jsonRpcConfig;
    private readonly IJsonRpcLocalStats _jsonRpcLocalStats = jsonRpcLocalStats;
    private readonly IRpcAuthentication? _rpcAuthentication = rpcAuthentication;
    private readonly ILogger _logger = logManager.GetClassLogger<JsonRpcHttpFastPath>();

    public bool CanProcess(IFeatureCollection features)
    {
        if (_jsonRpcProcessor.ProcessExit.IsCancellationRequested)
        {
            return false;
        }

        IHttpRequestFeature? request = features.Get<IHttpRequestFeature>();
        IHttpConnectionFeature? connection = features.Get<IHttpConnectionFeature>();
        return request is not null &&
               connection is not null &&
               request.Method == "POST" &&
               IsJsonContent(request.Headers) &&
               _jsonRpcUrlCollection.TryGetValue(connection.LocalPort, out JsonRpcUrl jsonRpcUrl) &&
               jsonRpcUrl.RpcEndpoint.HasFlag(RpcEndpoint.Http) &&
               (jsonRpcUrl.IsAuthenticated ||
                (connection.RemoteIpAddress is not null && IsLocalhost(connection.RemoteIpAddress)));
    }

    public ValueTask<bool> TryProcessAsync(IFeatureCollection features)
    {
        if (!CanProcess(features))
        {
            return new ValueTask<bool>(false);
        }

        IHttpConnectionFeature connection = features.GetRequiredFeature<IHttpConnectionFeature>();
        _jsonRpcUrlCollection.TryGetValue(connection.LocalPort, out JsonRpcUrl jsonRpcUrl);
        if (jsonRpcUrl.MaxRequestBodySize is not null)
        {
            IHttpMaxRequestBodySizeFeature? maxRequestBodySizeFeature = features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxRequestBodySizeFeature is not null && !maxRequestBodySizeFeature.IsReadOnly)
            {
                maxRequestBodySizeFeature.MaxRequestBodySize = jsonRpcUrl.MaxRequestBodySize;
            }
        }

        IHttpRequestFeature request = features.GetRequiredFeature<IHttpRequestFeature>();
        if (jsonRpcUrl.IsAuthenticated)
        {
            return AuthenticateAndProcessAsync(features, jsonRpcUrl, request.Headers.Authorization);
        }

        IRequestBodyPipeFeature? requestBody = features.Get<IRequestBodyPipeFeature>();
        if (requestBody is null)
        {
            return new ValueTask<bool>(false);
        }

        return ProcessRequestAsync(features, requestBody.Reader, jsonRpcUrl);
    }

    private async ValueTask<bool> AuthenticateAndProcessAsync(IFeatureCollection features, JsonRpcUrl jsonRpcUrl, string? authorization)
    {
        if (_rpcAuthentication is null || !await _rpcAuthentication.Authenticate(authorization))
        {
            await WriteErrorResponseAsync(features, StatusCodes.Status401Unauthorized, ErrorCodes.InvalidRequest, "Authentication error");
            return true;
        }

        IRequestBodyPipeFeature? requestBody = features.Get<IRequestBodyPipeFeature>();
        return requestBody is not null && await ProcessRequestAsync(features, requestBody.Reader, jsonRpcUrl);
    }

    private async ValueTask<bool> ProcessRequestAsync(IFeatureCollection features, PipeReader reader, JsonRpcUrl jsonRpcUrl)
    {
        if (await TryProcessSingleRequestAsync(features, reader, jsonRpcUrl))
        {
            return true;
        }

        await ProcessGenericRequestAsync(features, reader, jsonRpcUrl);
        return true;
    }

    private async ValueTask<bool> TryProcessSingleRequestAsync(IFeatureCollection features, PipeReader reader, JsonRpcUrl jsonRpcUrl)
    {
        if (!TryGetContentLength(features.GetRequiredFeature<IHttpRequestFeature>().Headers, out long contentLength) ||
            contentLength <= 0 ||
            contentLength > int.MaxValue)
        {
            return false;
        }

        ReadResult readResult = await reader.ReadAtLeastAsync((int)contentLength);
        ReadOnlySequence<byte> buffer = readResult.Buffer;
        if (buffer.Length != contentLength)
        {
            reader.AdvanceTo(buffer.Start, buffer.End);
            return false;
        }

        JsonDocument? jsonDocument = null;
        try
        {
            jsonDocument = JsonDocument.Parse(buffer);
            if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                return false;
            }

            long startTime = Stopwatch.GetTimestamp();
            JsonRpcRequest request = DeserializeObject(jsonDocument.RootElement);
            Metrics.JsonRpcRequests++;
            using JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
            JsonRpcResponse response = await _jsonRpcService.SendRequestAsync(request, jsonRpcContext);
            bool isSuccess = response is not JsonRpcErrorResponse;
            if (isSuccess)
            {
                Metrics.JsonRpcSuccesses++;
            }
            else
            {
                Metrics.JsonRpcErrors++;
            }

            string reportMethod = response is JsonRpcErrorResponse { Error.Code: ErrorCodes.MethodNotFound }
                ? RpcReport.UnknownMethod
                : request.Method;
            JsonRpcResult result = JsonRpcResult.Single(
                response,
                new RpcReport(reportMethod, (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds, isSuccess));
            await WriteResultAsync(features, result, jsonRpcContext, startTime);
            reader.AdvanceTo(buffer.End);
            Interlocked.Add(ref Metrics.JsonRpcBytesReceivedHttp, contentLength);
            return true;
        }
        catch (JsonException ex)
        {
            reader.AdvanceTo(buffer.End);
            Metrics.JsonRpcRequestDeserializationFailures++;
            if (_logger.IsError) _logger.Error("Error during parsing/validation.", ex);
            await WriteErrorResponseAsync(features, StatusCodes.Status200OK, ErrorCodes.ParseError, "parse error");
            Interlocked.Add(ref Metrics.JsonRpcBytesReceivedHttp, contentLength);
            return true;
        }
        finally
        {
            jsonDocument?.Dispose();
        }
    }

    private async Task ProcessGenericRequestAsync(IFeatureCollection features, PipeReader reader, JsonRpcUrl jsonRpcUrl)
    {
        long startTime = Stopwatch.GetTimestamp();
        TryGetContentLength(features.GetRequiredFeature<IHttpRequestFeature>().Headers, out long knownContentLength);

        try
        {
            using JsonRpcContext jsonRpcContext = JsonRpcContext.Http(jsonRpcUrl);
            await foreach (JsonRpcResult result in _jsonRpcProcessor.ProcessAsync(reader, jsonRpcContext))
            {
                await WriteResultAsync(features, result, jsonRpcContext, startTime);
                break;
            }
        }
        catch (BadHttpRequestException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Couldn't read request.{Environment.NewLine}{e}");
            await WriteErrorResponseAsync(
                features,
                e.StatusCode,
                e.StatusCode == StatusCodes.Status413PayloadTooLarge ? ErrorCodes.LimitExceeded : ErrorCodes.InvalidRequest,
                e.Message);
        }
        finally
        {
            Interlocked.Add(ref Metrics.JsonRpcBytesReceivedHttp, knownContentLength);
        }
    }

    private async Task WriteResultAsync(IFeatureCollection features, JsonRpcResult result, JsonRpcContext jsonRpcContext, long startTime)
    {
        using (result)
        {
            IHttpResponseFeature response = features.GetRequiredFeature<IHttpResponseFeature>();
            response.StatusCode = Startup.GetStatusCode(result);
            response.Headers.ContentType = "application/json";
            if (!result.IsCollection &&
                result.Response is not null &&
                Startup.TryGetKnownSingleResponseContentLength(result.Response, out long contentLength))
            {
                response.Headers.ContentLength = contentLength;
            }

            IHttpResponseBodyFeature responseBody = features.GetRequiredFeature<IHttpResponseBodyFeature>();
            CountingWriter resultWriter = new CountingPipeWriter(responseBody.Writer);
            IStreamableResult? streamableResponse = result.Response is JsonRpcSuccessResponse { Result: IStreamableResult streamable }
                ? streamable
                : null;

            try
            {
                if (result.IsCollection)
                {
                    resultWriter.Write(JsonOpeningBracket);
                    bool first = true;
                    JsonRpcBatchResultAsyncEnumerator enumerator = result.BatchedResponses.GetAsyncEnumerator(CancellationToken.None);
                    try
                    {
                        while (await enumerator.MoveNextAsync())
                        {
                            JsonRpcResult.Entry entry = enumerator.Current;
                            using (entry)
                            {
                                if (!first) resultWriter.Write(JsonComma);
                                first = false;
                                Startup.WriteJsonRpcResponse(resultWriter, entry.Response);
                                _ = _jsonRpcLocalStats.ReportCall(entry.Report);

                                if (!jsonRpcContext.IsAuthenticated && resultWriter.WrittenCount > _jsonRpcConfig.MaxBatchResponseBodySize)
                                {
                                    if (_logger.IsWarn) _logger.Warn($"The max batch response body size exceeded. The current response size {resultWriter.WrittenCount}, and the config setting is JsonRpc.{nameof(_jsonRpcConfig.MaxBatchResponseBodySize)} = {_jsonRpcConfig.MaxBatchResponseBodySize}");
                                    enumerator.IsStopped = true;
                                }
                            }
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync();
                    }
                    resultWriter.Write(JsonClosingBracket);
                }
                else if (streamableResponse is not null)
                {
                    await Startup.WriteStreamableResponseAsync(resultWriter, result.Response, streamableResponse, CancellationToken.None);
                }
                else
                {
                    Startup.WriteJsonRpcResponse(resultWriter, result.Response);
                }
                await resultWriter.FlushAsync();
            }
            catch (Exception e) when (e is OperationCanceledException || e.InnerException is OperationCanceledException)
            {
                JsonRpcErrorResponse error = _jsonRpcService.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.");
                Startup.WriteJsonRpcResponse(resultWriter, error);
                await resultWriter.FlushAsync();
            }

            long handlingTimeMicroseconds = (long)Stopwatch.GetElapsedTime(startTime).TotalMicroseconds;
            _ = _jsonRpcLocalStats.ReportCall(result.IsCollection
                ? new RpcReport("# collection serialization #", handlingTimeMicroseconds, true)
                : result.Report.Value, handlingTimeMicroseconds, resultWriter.WrittenCount);
            Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, resultWriter.WrittenCount);
        }
    }

    private async Task WriteErrorResponseAsync(IFeatureCollection features, int statusCode, int errorCode, string message)
    {
        JsonRpcErrorResponse errorResponse = _jsonRpcService.GetErrorResponse(errorCode, message);
        IHttpResponseFeature response = features.GetRequiredFeature<IHttpResponseFeature>();
        response.StatusCode = statusCode;
        response.Headers.ContentType = "application/json";

        CountingWriter writer = new CountingPipeWriter(features.GetRequiredFeature<IHttpResponseBodyFeature>().Writer);
        Startup.WriteJsonRpcResponse(writer, errorResponse);
        await writer.FlushAsync();
        Interlocked.Add(ref Metrics.JsonRpcBytesSentHttp, writer.WrittenCount);
    }

    private static JsonRpcRequest DeserializeObject(JsonElement element)
    {
        string? jsonRpc = null;
        if (element.TryGetProperty("jsonrpc"u8, out JsonElement versionElement) && versionElement.ValueEquals("2.0"u8))
        {
            jsonRpc = "2.0";
        }

        object? id = null;
        if (element.TryGetProperty("id"u8, out JsonElement idElement))
        {
            if (idElement.ValueKind == JsonValueKind.Number)
            {
                if (idElement.TryGetInt64(out long idNumber))
                {
                    id = idNumber;
                }
                else if (idElement.TryGetDecimal(out decimal value))
                {
                    id = value;
                }
            }
            else
            {
                id = idElement.GetString();
            }
        }

        string? method = null;
        if (element.TryGetProperty("method"u8, out JsonElement methodElement))
        {
            method = JsonRpcMethodNameCache.Intern(methodElement);
        }

        if (!element.TryGetProperty("params"u8, out JsonElement paramsElement))
        {
            paramsElement = default;
        }

        return new JsonRpcRequest
        {
            JsonRpc = jsonRpc!,
            Id = id!,
            Method = method!,
            Params = paramsElement
        };
    }

    private static bool IsJsonContent(IHeaderDictionary headers) =>
        headers.TryGetValue("Content-Type", out StringValues values) &&
        values.Count > 0 &&
        values[0]?.Contains("application/json", StringComparison.Ordinal) == true;

    private static bool TryGetContentLength(IHeaderDictionary headers, out long contentLength)
    {
        contentLength = 0;
        return headers.TryGetValue("Content-Length", out StringValues values) &&
               values.Count > 0 &&
               long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out contentLength);
    }

    private static bool IsLocalhost(IPAddress remoteIp) =>
        IPAddress.IsLoopback(remoteIp) || remoteIp.Equals(IPAddress.IPv6Loopback);
}
