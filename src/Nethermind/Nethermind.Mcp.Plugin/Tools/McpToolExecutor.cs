// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Nethermind.Blockchain.Find;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Writes a tool payload; returns <see langword="null"/> on success or an error result to abort.</summary>
internal delegate CallToolResult? McpPayloadWriter<in TState>(Utf8JsonWriter writer, TState state);

/// <summary>
/// Runs MCP tool bodies against a rented production <see cref="IEthRpcModule"/> under the MCP concurrency, time and
/// result-size limits, and converts outcomes into <see cref="CallToolResult"/>s.
/// </summary>
/// <remarks>
/// <para>
/// The eth module methods are synchronous and do not accept a cancellation token, so a body cannot be interrupted. Each
/// body therefore runs on the thread pool while the caller waits at most <see cref="IMcpConfig.ToolTimeout"/> for it:
/// when that elapses the client gets a <c>timeout</c> error, but the body keeps its concurrency slot and its rented
/// module until the underlying call actually returns. Runaway work thus stays counted against
/// <see cref="IMcpConfig.MaxConcurrentToolCalls"/> (new calls fail fast with <c>resource_exhausted</c>) instead of
/// piling up, and a module is never returned to its pool while still in use. The underlying call itself is bounded by
/// the eth module's own <c>JsonRpc.Timeout</c>, which the configuration keeps at or above the tool timeout.
/// </para>
/// <para>
/// The body also receives a token that is cancelled on client cancellation or at the tool timeout, so it can stop at
/// its own checkpoints (for example between logs). Client cancellation propagates as
/// <see cref="OperationCanceledException"/>; every other failure becomes an error result, and unexpected exceptions are
/// only logged, never sent to the client.
/// </para>
/// </remarks>
internal sealed class McpToolExecutor(IRpcModuleProvider rpcModuleProvider, IMcpConfig config, ILogManager logManager)
{
    private const int MaxClientMessageLength = 512;
    private const string GenericInternalError = "The node failed to process the request.";

    private readonly int _maxConcurrentCalls = Math.Max(1, config.MaxConcurrentToolCalls);
    private readonly SemaphoreSlim _slots = new(Math.Max(1, config.MaxConcurrentToolCalls));
    private readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(Math.Max(1, config.ToolTimeout));
    private readonly int _maxResultSize = Math.Max(1, config.MaxResultSize);
    private readonly ILogger _logger = logManager.GetClassLogger<McpToolExecutor>();

    /// <summary>
    /// Rents the eth module for <paramref name="rpcMethod"/>, runs <paramref name="body"/> on it and returns its result.
    /// </summary>
    /// <param name="toolName">The MCP tool name, used in log messages.</param>
    /// <param name="rpcMethod">The eth JSON-RPC method whose pool and sharing mode the rental follows.</param>
    /// <param name="body">The tool body; it must finish using the module (including lazy results) before returning.</param>
    /// <param name="cancellationToken">The MCP request token.</param>
    /// <exception cref="OperationCanceledException">The client cancelled the request.</exception>
    public async Task<CallToolResult> ExecuteAsync(
        string toolName,
        string rpcMethod,
        Func<IEthRpcModule, CancellationToken, Task<CallToolResult>> body,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_slots.Wait(0))
        {
            return Error(McpToolErrorCodes.ResourceExhausted, $"Too many concurrent tool calls (limit {_maxConcurrentCalls}); retry later.");
        }

        // Deliberately not passing the token to Task.Run: the delegate must run to release the slot.
        Task<CallToolResult> work = Task.Run(() => RunAndReleaseAsync(toolName, rpcMethod, body, cancellationToken));

        try
        {
            return await work.WaitAsync(_timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return TimeoutError();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimeoutError();
        }
    }

    /// <summary>Builds a success result <c>{"result": value}</c> using the JSON-RPC serializer.</summary>
    public CallToolResult Success(object? value) => Success(value, static (writer, v) =>
    {
        WriteValue(writer, v);
        return null;
    });

    /// <summary>Builds a success result <c>{"result": ...}</c> whose payload is produced by <paramref name="writePayload"/>.</summary>
    /// <remarks>Fails with <c>resource_exhausted</c> once the serialized result exceeds <see cref="IMcpConfig.MaxResultSize"/>.</remarks>
    public CallToolResult Success<TState>(TState state, McpPayloadWriter<TState> writePayload)
    {
        using LimitedPooledBufferWriter buffer = new(_maxResultSize);
        try
        {
            using (Utf8JsonWriter writer = new(buffer, CreateWriterOptions()))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("result"u8);
                CallToolResult? abort = writePayload(writer, state);
                if (abort is not null)
                {
                    return abort;
                }

                writer.WriteEndObject();
            }

            return CreateResult(buffer.WrittenSpan, isError: false);
        }
        catch (ResultTooLargeException)
        {
            return Error(McpToolErrorCodes.ResourceExhausted, $"The result exceeds the {_maxResultSize}-byte limit; request less data.");
        }
    }

    /// <summary>Serializes <paramref name="value"/> exactly as JSON-RPC would, or writes <c>null</c>.</summary>
    public static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), EthereumJsonSerializer.JsonOptions);
    }

    /// <summary>Maps a failed <see cref="IResultWrapper"/> to an MCP error result.</summary>
    public CallToolResult Failure(string toolName, IResultWrapper wrapper)
    {
        int code = wrapper.ErrorCode;
        string message = Sanitize(wrapper.Result.Error);

        string mapped = code switch
        {
            ErrorCodes.ExecutionReverted => McpToolErrorCodes.ExecutionReverted,
            _ when wrapper.IsTemporary => McpToolErrorCodes.Unavailable,
            // -32000 is shared by "resource not found", "invalid input" and EVM execution errors.
            ErrorCodes.Default => message == BlockFinderExtensions.HeaderNotFound ? McpToolErrorCodes.NotFound : McpToolErrorCodes.InvalidInput,
            ErrorCodes.InvalidParams or ErrorCodes.InvalidRequest or ErrorCodes.ParseError => McpToolErrorCodes.InvalidInput,
            ErrorCodes.ResourceUnavailable or ErrorCodes.PrunedHistoryUnavailable or ErrorCodes.UnavailableBeforeFork
                or ErrorCodes.UnknownBlockError or ErrorCodes.MethodNotFound => McpToolErrorCodes.Unavailable,
            ErrorCodes.LimitExceeded or ErrorCodes.ModuleTimeout or ErrorCodes.ClientLimitExceededError => McpToolErrorCodes.ResourceExhausted,
            ErrorCodes.Timeout => McpToolErrorCodes.Timeout,
            _ => McpToolErrorCodes.InternalError
        };

        if (mapped == McpToolErrorCodes.InternalError)
        {
            if (_logger.IsWarn) _logger.Warn($"MCP tool {toolName} failed with JSON-RPC error {code}: {message}");
            return Error(McpToolErrorCodes.InternalError, GenericInternalError);
        }

        if (mapped == McpToolErrorCodes.Unavailable && wrapper.IsTemporary)
        {
            message = $"{message} (the node is still syncing)";
        }

        string? data = mapped == McpToolErrorCodes.ExecutionReverted && wrapper.HasErrorData && wrapper.Data is string revertData
            ? revertData
            : null;
        return Error(mapped, message.Length == 0 ? mapped : message, data);
    }

    /// <summary>Builds an error result <c>{"error": {"code", "message", "data"?}}</c>.</summary>
    public static CallToolResult Error(string code, string message, string? data = null)
    {
        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = new(buffer, CreateWriterOptions()))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("error"u8);
            writer.WriteStartObject();
            writer.WriteString("code"u8, code);
            writer.WriteString("message"u8, message);
            if (data is not null)
            {
                writer.WriteString("data"u8, data);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return CreateResult(buffer.WrittenSpan, isError: true);
    }

    private async Task<CallToolResult> RunAndReleaseAsync(
        string toolName,
        string rpcMethod,
        Func<IEthRpcModule, CancellationToken, Task<CallToolResult>> body,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            return await RunRentedAsync(toolName, rpcMethod, body, cts.Token);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<CallToolResult> RunRentedAsync(
        string toolName,
        string rpcMethod,
        Func<IEthRpcModule, CancellationToken, Task<CallToolResult>> body,
        CancellationToken cancellationToken)
    {
        RpcModuleProvider.ResolvedMethodInfo? method = null;
        IRpcModule? module = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Renting by resolved method uses the method's [JsonRpcMethod(IsSharable)] flag, exactly like JsonRpcService.
            method = rpcModuleProvider.Resolve(rpcMethod);
            if (method is null)
            {
                return Error(McpToolErrorCodes.Unavailable, $"The {rpcMethod} JSON-RPC method is not available on this node.");
            }

            module = await rpcModuleProvider.Rent(method);
            if (module is not IEthRpcModule ethModule)
            {
                return Error(McpToolErrorCodes.Unavailable, $"The eth JSON-RPC module is not available on this node.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await body(ethModule, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ModuleRentalTimeoutException or LimitExceededException)
        {
            return Error(McpToolErrorCodes.ResourceExhausted, "The node is busy serving other RPC requests; retry later.");
        }
        catch (ResourceNotFoundException)
        {
            return Error(McpToolErrorCodes.Unavailable, "The requested history is not available on this node.");
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"MCP tool {toolName} failed: {ex}");
            return Error(McpToolErrorCodes.InternalError, GenericInternalError);
        }
        finally
        {
            if (module is not null)
            {
                rpcModuleProvider.Return(method!, module);
            }
        }
    }

    private CallToolResult TimeoutError() =>
        Error(McpToolErrorCodes.Timeout, $"The tool did not complete within {(long)_timeout.TotalMilliseconds} ms; request less data or retry later.");

    private static JsonWriterOptions CreateWriterOptions()
    {
        JsonSerializerOptions options = EthereumJsonSerializer.JsonOptions;
        return new JsonWriterOptions { Encoder = options.Encoder, MaxDepth = options.MaxDepth };
    }

    private static CallToolResult CreateResult(ReadOnlySpan<byte> json, bool isError)
    {
        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        return new CallToolResult
        {
            IsError = isError ? true : null,
            StructuredContent = document.RootElement.Clone(),
            Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(json) }]
        };
    }

    private static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        int length = Math.Min(message.Length, MaxClientMessageLength);
        StringBuilder builder = new(length);
        for (int i = 0; i < length; i++)
        {
            char c = message[i];
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }
}
