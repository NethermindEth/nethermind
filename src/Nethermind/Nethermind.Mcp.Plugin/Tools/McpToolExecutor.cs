// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Reflection;
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
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Writes a tool payload; returns <see langword="null"/> on success or an error result to abort.</summary>
internal delegate CallToolResult? McpPayloadWriter<in TState>(Utf8JsonWriter writer, TState state);

/// <summary>
/// Runs MCP tool bodies under the MCP concurrency, time and result-size limits, either against a production JSON-RPC
/// module rented from <see cref="IRpcModuleProvider"/> (<c>eth</c>, <c>trace</c>, <c>debug</c>, ...) or, for tools that
/// only read in-process services, without a module (<see cref="ExecuteLocalAsync"/>), and converts outcomes into
/// <see cref="CallToolResult"/>s.
/// </summary>
/// <remarks>
/// <para>
/// Most RPC module methods are synchronous and do not accept a cancellation token, so a body cannot be interrupted. Each
/// body therefore runs on the thread pool while the caller waits at most <see cref="IMcpConfig.ToolTimeout"/> for it:
/// when that elapses the client gets a <c>timeout</c> error, but the body keeps its concurrency slot and its rented
/// module until the underlying call actually returns. Runaway work thus stays counted against
/// <see cref="IMcpConfig.MaxConcurrentToolCalls"/> (new calls fail fast with <c>resource_exhausted</c>) instead of
/// piling up, and a module is never returned to its pool while still in use. The underlying call itself is bounded by
/// the module's own <c>JsonRpc.Timeout</c>, which the configuration keeps at or above the tool timeout.
/// </para>
/// <para>
/// The body also receives a token that is cancelled on client cancellation or at the tool timeout, so it can stop at
/// its own checkpoints (for example between logs). Client cancellation propagates as
/// <see cref="OperationCanceledException"/>; every other failure becomes an error result, and unexpected exceptions are
/// only logged, never sent to the client.
/// </para>
/// <para>
/// Data this node does not hold maps to <c>unavailable</c> with a hint naming what the node does keep (from
/// <see cref="McpNodeCapabilities"/>): JSON-RPC <c>-32002</c> "No state available", EIP-4444 <c>4444</c> pruned history,
/// <c>-32000</c> "missing trie node", and the <see cref="MissingTrieNodeException"/>, <see cref="StateUnavailableException"/>
/// and <see cref="ResourceNotFoundException"/> exceptions a body may throw.
/// </para>
/// </remarks>
internal sealed class McpToolExecutor(
    IRpcModuleProvider rpcModuleProvider,
    IMcpConfig config,
    ILogManager logManager,
    McpNodeCapabilities? capabilities = null)
{
    private const int MaxClientMessageLength = 512;

    /// <summary>The most bytes of revert data an error carries; longer data is cut and flagged with <c>dataTruncated</c>.</summary>
    public const int MaxErrorDataBytes = 4096;
    private const string GenericInternalError = "The node failed to process the request.";
    private const string StateAdvice = "Use a recent block (for example \"latest\"), or query an archive node.";
    private const string HistoryAdvice = "Use a more recent block, or query a node that keeps full history.";

    private readonly int _maxConcurrentCalls = Math.Max(1, config.MaxConcurrentToolCalls);
    private readonly SemaphoreSlim _slots = new(Math.Max(1, config.MaxConcurrentToolCalls));
    // Module-less tools (node_status) get their own slot, so slow module-backed calls cannot starve the health check.
    private readonly SemaphoreSlim _localSlot = new(1);
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
    public Task<CallToolResult> ExecuteAsync(
        string toolName,
        string rpcMethod,
        Func<IEthRpcModule, CancellationToken, Task<CallToolResult>> body,
        CancellationToken cancellationToken) =>
        ExecuteAsync<IEthRpcModule>(toolName, rpcMethod, body, cancellationToken);

    /// <summary>
    /// Rents the <typeparamref name="TModule"/> module serving <paramref name="rpcMethod"/>, runs <paramref name="body"/> on it
    /// and returns its result, under the MCP concurrency and time limits.
    /// </summary>
    /// <typeparam name="TModule">The JSON-RPC module interface that declares <paramref name="rpcMethod"/>.</typeparam>
    /// <param name="toolName">The MCP tool name, used in log messages.</param>
    /// <param name="rpcMethod">The JSON-RPC method whose pool and sharing mode the rental follows.</param>
    /// <param name="body">The tool body; it must finish using the module (including lazy results) before returning.</param>
    /// <param name="cancellationToken">The MCP request token.</param>
    /// <exception cref="OperationCanceledException">The client cancelled the request.</exception>
    public Task<CallToolResult> ExecuteAsync<TModule>(
        string toolName,
        string rpcMethod,
        Func<TModule, CancellationToken, Task<CallToolResult>> body,
        CancellationToken cancellationToken) where TModule : class, IRpcModule =>
        RunLimitedAsync(_slots, _maxConcurrentCalls, token => RunRentedAsync(toolName, rpcMethod, body, token), cancellationToken);

    /// <summary>
    /// Runs a module-less <paramref name="body"/> (one that only reads in-process services such as the block tree or sync
    /// state) under the same timeout and error mapping as <see cref="ExecuteAsync{TModule}"/>.
    /// </summary>
    /// <remarks>
    /// Meant for cheap, bounded bodies. They run in one reserved slot outside <see cref="IMcpConfig.MaxConcurrentToolCalls"/>,
    /// so a health check still answers while slow module-backed calls hold every shared slot.
    /// </remarks>
    /// <param name="toolName">The MCP tool name, used in log messages.</param>
    /// <param name="body">The tool body.</param>
    /// <param name="cancellationToken">The MCP request token.</param>
    /// <exception cref="OperationCanceledException">The client cancelled the request.</exception>
    public Task<CallToolResult> ExecuteLocalAsync(
        string toolName,
        Func<CancellationToken, Task<CallToolResult>> body,
        CancellationToken cancellationToken) =>
        RunLimitedAsync(_localSlot, 1, token => RunLocalAsync(toolName, body, token), cancellationToken);

    private async Task<CallToolResult> RunLimitedAsync(SemaphoreSlim slots, int limit, Func<CancellationToken, Task<CallToolResult>> run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!slots.Wait(0))
        {
            return Error(McpToolErrorCodes.ResourceExhausted, $"Too many concurrent tool calls (limit {limit}); retry later.");
        }

        // Deliberately not passing the token to Task.Run: the delegate must run to release the slot.
        Task<CallToolResult> work = Task.Run(() => RunAndReleaseAsync(slots, run, cancellationToken));

        try
        {
            return LimitErrorSize(await work.WaitAsync(_timeout, cancellationToken));
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
            // A trie node pruned under a state root that passed the HasStateForBlock guard is answered with -32000.
            ErrorCodes.Default when message.Contains("missing trie node", StringComparison.OrdinalIgnoreCase) => McpToolErrorCodes.Unavailable,
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

        if (mapped == McpToolErrorCodes.Unavailable)
        {
            if (wrapper.IsTemporary)
            {
                message = $"{message} (the node is still syncing)";
            }

            message = AddAvailabilityHint(code, message);
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
            WriteErrorData(writer, data);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return CreateResult(buffer.WrittenSpan, isError: true);
    }

    /// <summary>Writes the <c>data</c> member of an error (0x-hex revert data), cut to <see cref="MaxErrorDataBytes"/> bytes.</summary>
    /// <remarks>Cut data is followed by <c>"dataTruncated": true</c> and <c>"dataSize"</c>, the full size in bytes.</remarks>
    public static void WriteErrorData(Utf8JsonWriter writer, string? data)
    {
        if (data is null)
        {
            return;
        }

        const int maxLength = 2 + MaxErrorDataBytes * 2;
        if (data.Length <= maxLength)
        {
            writer.WriteString("data"u8, data);
            return;
        }

        writer.WriteString("data"u8, data.AsSpan(0, maxLength));
        writer.WriteBoolean("dataTruncated"u8, true);
        writer.WriteNumber("dataSize"u8, (data.Length - 2) / 2);
    }

    /// <summary>Returns the <c>error</c> object of an error result built by this executor, or <see langword="null"/> for any other result.</summary>
    public static JsonElement? ReadError(CallToolResult result)
    {
        if (result.IsError != true || result.Content is not [TextContentBlock { Text: { } text }, ..])
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("error", out JsonElement error) ? error.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<CallToolResult> RunAndReleaseAsync(SemaphoreSlim slots, Func<CancellationToken, Task<CallToolResult>> run, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            return await run(cts.Token);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task<CallToolResult> RunRentedAsync<TModule>(
        string toolName,
        string rpcMethod,
        Func<TModule, CancellationToken, Task<CallToolResult>> body,
        CancellationToken cancellationToken) where TModule : class, IRpcModule
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
            if (module is not TModule typedModule)
            {
                return Error(McpToolErrorCodes.Unavailable, $"The JSON-RPC module serving {rpcMethod} is not available on this node.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await body(typedModule, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapException(toolName, ex);
        }
        finally
        {
            if (module is not null)
            {
                rpcModuleProvider.Return(method!, module);
            }
        }
    }

    private async Task<CallToolResult> RunLocalAsync(string toolName, Func<CancellationToken, Task<CallToolResult>> body, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await body(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MapException(toolName, ex);
        }
    }

    private CallToolResult MapException(string toolName, Exception ex)
    {
        switch (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex)
        {
            case ModuleRentalTimeoutException or LimitExceededException:
                return Error(McpToolErrorCodes.ResourceExhausted, "The node is busy serving other RPC requests; retry later.");
            case ResourceNotFoundException:
                return Error(McpToolErrorCodes.Unavailable, $"The requested history is not available on this node{HistoryHint()}");
            case MissingTrieNodeException { InnerException: not StateNotRetainedException } missing:
                // Unlike state that was never retained, a node missing under a root that passed the state guard may be
                // corruption, so operators get the same warning the JSON-RPC service logs.
                if (_logger.IsWarn) _logger.Warn($"Missing trie node during MCP tool {toolName}: {missing.Message}");
                return Error(McpToolErrorCodes.Unavailable, $"Part of the state needed for this request is not available on this node{StateHint()}");
            case MissingTrieNodeException or StateUnavailableException:
                if (_logger.IsDebug) _logger.Debug($"MCP tool {toolName}: state unavailable: {ex.Message}");
                return Error(McpToolErrorCodes.Unavailable, $"The state needed for this request is not available on this node{StateHint()}");
            default:
                if (_logger.IsWarn) _logger.Warn($"MCP tool {toolName} failed: {ex}");
                return Error(McpToolErrorCodes.InternalError, GenericInternalError);
        }
    }

    // Appends what the node does keep to an unavailable-data message from a JSON-RPC module.
    private string AddAvailabilityHint(int code, string message)
    {
        if (code == ErrorCodes.PrunedHistoryUnavailable || message.StartsWith(ErrorMessages.PrunedHistoryUnavailable, StringComparison.OrdinalIgnoreCase))
        {
            return $"{message}{HistoryHint()}";
        }

        return message.Contains("state", StringComparison.OrdinalIgnoreCase) || message.Contains("trie node", StringComparison.OrdinalIgnoreCase)
            ? $"{message}{StateHint()}"
            : message;
    }

    private string StateHint() =>
        TryGetAvailability() is { } availability
            ? $": this node {McpNodeCapabilities.DescribeStateRange(availability)}. {StateAdvice}"
            : $". {StateAdvice}";

    private string HistoryHint() =>
        TryGetAvailability() is { } availability
            ? $": this node {McpNodeCapabilities.DescribeHistoryRange(availability)}. {HistoryAdvice}"
            : $". {HistoryAdvice}";

    private McpDataAvailability? TryGetAvailability()
    {
        try
        {
            return capabilities?.GetAvailability();
        }
        catch (Exception ex)
        {
            // A hint must never turn a clean unavailable error into an internal error.
            if (_logger.IsDebug) _logger.Debug($"MCP could not read data availability: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Rents an additional module for use inside a running tool body, without taking another concurrency slot.
    /// </summary>
    /// <remarks>
    /// Only call this from a body started by <see cref="ExecuteAsync{TModule}"/>; the lease must be disposed before the body
    /// returns so the module goes back to its pool. Rental failures surface as the same exceptions
    /// <see cref="ExecuteAsync{TModule}"/> maps to error results.
    /// </remarks>
    /// <returns>A lease whose <see cref="ModuleLease{TModule}.Module"/> is <see langword="null"/> if the method is unavailable.</returns>
    public async Task<ModuleLease<TModule>> RentAsync<TModule>(string rpcMethod) where TModule : class, IRpcModule
    {
        RpcModuleProvider.ResolvedMethodInfo? method = rpcModuleProvider.Resolve(rpcMethod);
        if (method is null)
        {
            return default;
        }

        IRpcModule module = await rpcModuleProvider.Rent(method);
        if (module is TModule typed)
        {
            return new ModuleLease<TModule>(rpcModuleProvider, method, typed);
        }

        if (module is not null)
        {
            rpcModuleProvider.Return(method, module);
        }

        return default;
    }

    private CallToolResult TimeoutError() =>
        Error(McpToolErrorCodes.Timeout, $"The tool did not complete within {(long)_timeout.TotalMilliseconds} ms; request less data or retry later.");

    private static JsonWriterOptions CreateWriterOptions()
    {
        JsonSerializerOptions options = EthereumJsonSerializer.JsonOptions;
        return new JsonWriterOptions { Encoder = options.Encoder, MaxDepth = options.MaxDepth };
    }

    // Errors are text-only: structuredContent must match the tool's output schema, which describes the success shape.
    private static CallToolResult CreateResult(ReadOnlySpan<byte> json, bool isError)
    {
        TextContentBlock text = new() { Text = Encoding.UTF8.GetString(json) };
        if (isError)
        {
            return new CallToolResult { IsError = true, Content = [text] };
        }

        using JsonDocument document = JsonDocument.Parse(json.ToArray());
        return new CallToolResult { StructuredContent = document.RootElement.Clone(), Content = [text] };
    }

    // Error payloads are small by construction (bounded message and data), but a tiny MaxResultSize must still hold.
    private CallToolResult LimitErrorSize(CallToolResult result)
    {
        if (result.IsError != true || result.Content is not [TextContentBlock { Text: { } text }] || Encoding.UTF8.GetByteCount(text) <= _maxResultSize)
        {
            return result;
        }

        string code = ReadError(result)?.GetProperty("code").GetString() ?? McpToolErrorCodes.InternalError;
        return Error(code, $"The error details exceed the {_maxResultSize}-byte result limit.");
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
            builder.Append(McpTokenMetadata.IsUnsafeChar(c) ? ' ' : c);
        }

        return builder.ToString();
    }
}

/// <summary>A module rented by <see cref="McpToolExecutor.RentAsync{TModule}"/>; disposing it returns the module to its pool.</summary>
internal readonly struct ModuleLease<TModule>(IRpcModuleProvider provider, RpcModuleProvider.ResolvedMethodInfo method, TModule module) : IDisposable
    where TModule : class, IRpcModule
{
    /// <summary>Gets the rented module, or <see langword="null"/> when the method is unavailable on this node.</summary>
    public TModule? Module => module;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (module is not null)
        {
            provider.Return(method, module);
        }
    }
}
