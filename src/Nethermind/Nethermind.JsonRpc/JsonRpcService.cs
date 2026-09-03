// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Exceptions;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.Trie;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider.ResolvedMethodInfo;

namespace Nethermind.JsonRpc;

public sealed class JsonRpcService(IRpcModuleProvider rpcModuleProvider, ILogManager logManager, IJsonRpcConfig jsonRpcConfig) : IJsonRpcService
{
    private const int MaxPooledParameterCount = 8;
    private const int MaxReportedExceptionChainDepth = 8;

    private readonly ILogger _logger = logManager.GetClassLogger<JsonRpcService>();
    private readonly IRpcModuleProvider _rpcModuleProvider = rpcModuleProvider;
    private readonly HashSet<string> _methodsLoggingFiltering = [.. jsonRpcConfig.MethodsLoggingFiltering ?? []];
    private readonly int _maxLoggedRequestParametersCharacters = jsonRpcConfig.MaxLoggedRequestParametersCharacters ?? int.MaxValue;

    public ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
    {
        (int? errorCode, string? errorMessage, string methodName, ResolvedMethodInfo? method, bool operatorActionable) = Validate(rpcRequest, context);
        if (errorCode.HasValue)
        {
            if (_logger.IsDebug) _logger.Debug($"Validation error when handling request: {rpcRequest}");
            JsonRpcErrorResponse errorResponse = GetErrorResponse(methodName, errorCode.Value, errorMessage, null, in rpcRequest.IdRef);
            if (operatorActionable && errorResponse.Error is not null)
            {
                errorResponse.Error.OperatorActionable = true;
            }

            return ValueTask.FromResult<JsonRpcResponse>(errorResponse);
        }

        try
        {
            ValueTask<JsonRpcResponse> responseTask = ExecuteAsync(rpcRequest, methodName, method!, context);
            return responseTask.IsCompletedSuccessfully
                ? responseTask
                : AwaitRequestAsync(responseTask, rpcRequest);
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult<JsonRpcResponse>(ReturnErrorResponse(rpcRequest, ex));
        }

        async ValueTask<JsonRpcResponse> AwaitRequestAsync(ValueTask<JsonRpcResponse> responseTask, JsonRpcRequest rpcRequest)
        {
            try
            {
                return await responseTask;
            }
            catch (Exception ex)
            {
                return ReturnErrorResponse(rpcRequest, ex);
            }
        }
    }

    private JsonRpcErrorResponse ReturnErrorResponse(JsonRpcRequest rpcRequest, Exception ex)
    {
        // Unwrap reflection-wrapped exceptions so the switch below sees the real type.
        if (ex is TargetInvocationException { InnerException: { } inner })
        {
            ex = inner;
        }

        (int errorCode, string errorText, bool suppressWarning) = ex switch
        {
            // suppressWarning doubles as the overload-shedding marker: GetErrorResponse counts
            // suppressed LimitExceeded/ModuleTimeout responses in Metrics.JsonRpcOverloadRejections.
            LimitExceededException or ConcurrencyLimitReachedException => (ErrorCodes.LimitExceeded, "Too many requests", true),
            ModuleRentalTimeoutException => (ErrorCodes.ModuleTimeout, "Timeout", true),
            _ => (ErrorCodes.InternalError, "Internal error", false),
        };

        if (!suppressWarning && _logger.IsError) _logger.Error($"Error during method execution, request: {DescribeForErrorLog(rpcRequest, ex)}", ex);
        return GetErrorResponse(rpcRequest.Method, errorCode, errorText, suppressWarning ? null : GetExceptionText(ex), in rpcRequest.IdRef, suppressWarning: suppressWarning);
    }

    // Formatting the request parses and stringifies its params, which for engine_newPayload is a
    // multi-megabyte payload. When the heap is already exhausted that would just throw again.
    private static string DescribeForErrorLog(JsonRpcRequest request, Exception ex) =>
        ex is OutOfMemoryException or { InnerException: OutOfMemoryException }
            ? $"Id:{request.Id}, {request.Method}(params omitted)"
            : request.ToString();

    private async ValueTask<JsonRpcResponse> ExecuteAsync(JsonRpcRequest request, string methodName, ResolvedMethodInfo method, JsonRpcContext context)
    {
        const string GetLogsMethodName = "eth_getLogs";

        JsonRpcErrorResponse? value = PrepareParameters(
            request,
            methodName,
            method,
            out object?[]? parameters,
            out int parameterCount,
            out bool returnParametersToPool);
        if (value is not null)
        {
            return value;
        }

        IRpcModule rpcModule = await _rpcModuleProvider.Rent(method);
        if (rpcModule is IContextAwareRpcModule contextAwareModule)
        {
            contextAwareModule.Context = context;
        }
        void ReturnRental() => _rpcModuleProvider.Return(method, rpcModule);
        bool returnImmediately = methodName != GetLogsMethodName;
        Action? returnAction = returnImmediately ? null : ReturnRental;
        IResultWrapper? resultWrapper = null;
        try
        {
            object? invocationResult = parameterCount switch
            {
                0 when method.DirectNoParameterInvoker is { } directInvoker => directInvoker(rpcModule),
                > 0 when method.DirectParameterInvoker is { } directInvoker => directInvoker(rpcModule, parameters!),
                _ => method.Invoker.Invoke(rpcModule, parameters.AsSpan(0, parameterCount)),
            };
            ReturnParameters(parameters, returnParametersToPool);

            switch (invocationResult)
            {
                case IResultWrapper wrapper:
                    resultWrapper = wrapper;
                    break;
                case Task task:
                    await task;
                    resultWrapper = method.ReadTaskResult(task);
                    break;
                default:
                    break;
            }

            // A streamed result executes while the response is written, after this method has returned, on state the
            // module owns (its overridable world state env). Returning the module now would let the next rental run on
            // that same env concurrently, so the rental has to last until the response is disposed.
            if (returnImmediately && resultWrapper is JsonRpcResponse invocationResponse && invocationResponse.TryGetStreamableResult(out _))
            {
                returnImmediately = false;
                returnAction = ReturnRental;
            }
        }
        catch (Exception ex)
        {
            return HandleInvocationException(ex, methodName, request, returnAction);
        }
        finally
        {
            if (returnImmediately)
            {
                ReturnRental();
            }
        }

        if (resultWrapper is null)
        {
            return HandleMissingResultWrapper(request, methodName, returnAction);
        }

        if (resultWrapper is JsonRpcResponse response)
        {
            return response.WithResponseContext(in request.IdRef, returnAction);
        }

        return HandleUnsupportedResultWrapper(request, methodName, returnAction);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsonRpcResponse HandleMissingResultWrapper(JsonRpcRequest request, string methodName, Action? returnAction)
    {
        string errorMessage = $"Method {methodName} execution result does not implement IResultWrapper";
        if (_logger.IsError) _logger.Error(errorMessage);
        return GetErrorResponse(methodName, ErrorCodes.InternalError, errorMessage, null, in request.IdRef, returnAction);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsonRpcResponse HandleUnsupportedResultWrapper(JsonRpcRequest request, string methodName, Action? returnAction)
    {
        string errorMessage = $"Method {methodName} execution result implements IResultWrapper but not JsonRpcResponse";
        if (_logger.IsError) _logger.Error(errorMessage);
        return GetErrorResponse(methodName, ErrorCodes.InternalError, errorMessage, null, in request.IdRef, returnAction);
    }

    private static void ReturnParameters(object?[]? parameters, bool returnToPool)
    {
        if (returnToPool && parameters is not null)
        {
            ArrayPool<object?>.Shared.Return(parameters, clearArray: true);
        }
    }

    private JsonRpcErrorResponse? PrepareParameters(
        JsonRpcRequest request,
        string methodName,
        ResolvedMethodInfo method,
        out object?[]? parameters,
        out int parameterCount,
        out bool returnParametersToPool)
    {
        parameters = null;
        parameterCount = 0;
        returnParametersToPool = false;
        ReadOnlyMemory<byte> providedParametersUtf8 = request.ParamsUtf8;
        ExpectedParameter[] expectedParameters = method.ExpectedParameters;
        bool useUtf8Parameters = CanDeserializeParametersFromUtf8(request, expectedParameters);
        JsonElement providedParameters = useUtf8Parameters ? default : request.Params;

        LogRequest(methodName, expectedParameters, useUtf8Parameters, providedParametersUtf8, providedParameters);

        return expectedParameters.Length == 0
            ? PrepareNoParameters(
                request,
                methodName,
                useUtf8Parameters,
                providedParametersUtf8,
                providedParameters,
                out parameters)
            : PrepareNonEmptyParameters(
                request,
                methodName,
                expectedParameters,
                useUtf8Parameters,
                providedParametersUtf8,
                providedParameters,
                out parameters,
                out parameterCount,
                out returnParametersToPool);
    }

    private JsonRpcErrorResponse? PrepareNoParameters(
        JsonRpcRequest request,
        string methodName,
        bool useUtf8Parameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        JsonElement providedParameters,
        out object?[] parameters)
    {
        parameters = [];
        if (HasUnexpectedZeroParameterArray(useUtf8Parameters, providedParametersUtf8, providedParameters))
        {
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, in request.IdRef);
        }

        return null;
    }

    private JsonRpcErrorResponse? PrepareNonEmptyParameters(
        JsonRpcRequest request,
        string methodName,
        ExpectedParameter[] expectedParameters,
        bool useUtf8Parameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        JsonElement providedParameters,
        out object?[]? parameters,
        out int parameterCount,
        out bool returnParametersToPool)
    {
        parameters = null;
        parameterCount = 0;
        returnParametersToPool = false;
        try
        {
            return useUtf8Parameters
                ? PrepareUtf8Parameters(
                    expectedParameters,
                    methodName,
                    in request.IdRef,
                    providedParametersUtf8,
                    out parameters,
                    out parameterCount,
                    out returnParametersToPool)
                : PrepareJsonElementParameters(
                    expectedParameters,
                    methodName,
                    in request.IdRef,
                    providedParameters,
                    providedParametersUtf8,
                    out parameters,
                    out parameterCount,
                    out returnParametersToPool);
        }
        catch (Exception e)
        {
            ReturnParameters(parameters, returnParametersToPool);
            // A fault the params cannot cause is a condition of this node, and the catch is deliberately broad
            // enough to swallow one. It keeps the operator's line, and marks the response so the processor does
            // not demote it either - -32602 alone would otherwise read as the caller's fault at both sites.
            if (IsNodeFault(e))
            {
                // Formatting the params would allocate on an already exhausted heap, so they are omitted and the
                // exception is passed to the logger rather than interpolated (same reason as DescribeForErrorLog).
                if (_logger.IsError) _logger.Error($"Failed to bind JSON RPC parameters for {methodName}", e);
                JsonRpcErrorResponse nodeFault = GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, in request.IdRef);
                if (nodeFault.Error is not null) nodeFault.Error.OperatorActionable = true;
                return nodeFault;
            }

            // Caller-supplied params that fail to bind are answered with -32602; the echo of the params and the
            // exception (with its stack trace) is Debug-only detail, not an operator warning (#13156).
            if (_logger.IsDebug) _logger.Debug($"Incorrect JSON RPC parameters when calling {methodName} with params [{GetParamsForLog(request)}] {e}");
            string message = GetSafePublicMessage(e) ?? "Invalid params";
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams, message, null, in request.IdRef);
        }
    }

    /// <summary>
    /// Whether the fault is a condition of this node rather than something the caller's params can cause.
    /// </summary>
    /// <remarks>
    /// A deny-list of two, not an allow-list of expected binding failures: a converter can throw anything, and
    /// promoting an unanticipated caller-fault type back into a per-request WARN is what #13156 was about.
    /// </remarks>
    private static bool IsNodeFault(Exception e)
    {
        for (Exception? ex = e; ex is not null; ex = ex.InnerException)
        {
            if (ex is OutOfMemoryException or ObjectDisposedException) return true;
        }

        return false;
    }

    private JsonRpcErrorResponse? PrepareUtf8Parameters(
        ExpectedParameter[] expectedParameters,
        string methodName,
        in JsonRpcId requestId,
        ReadOnlyMemory<byte> providedParametersUtf8,
        out object?[]? parameters,
        out int parameterCount,
        out bool returnParametersToPool)
    {
        parameters = DeserializeParameters(
            expectedParameters,
            providedParametersUtf8,
            out int providedParametersLength,
            out int missingParamsCount,
            out int missingRequiredParameterIndex,
            out ExceptionDispatchInfo? parameterDeserializationException,
            out returnParametersToPool);

        JsonRpcErrorResponse? validationError = ValidateMissingParameters(
            expectedParameters,
            methodName,
            in requestId,
            providedParametersLength,
            missingRequiredParameterIndex,
            ref missingParamsCount);
        if (validationError is not null)
        {
            ReturnParameters(parameters, returnParametersToPool);
            parameters = null;
            returnParametersToPool = false;
            parameterCount = 0;
            return validationError;
        }

        parameterDeserializationException?.Throw();
        parameterCount = Math.Min(expectedParameters.Length, providedParametersLength + missingParamsCount);
        FillDefaultParameters(expectedParameters, parameters, providedParametersLength, parameterCount);
        return null;
    }

    private JsonRpcErrorResponse? PrepareJsonElementParameters(
        ExpectedParameter[] expectedParameters,
        string methodName,
        in JsonRpcId requestId,
        JsonElement providedParameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        out object?[]? parameters,
        out int parameterCount,
        out bool returnParametersToPool)
    {
        parameters = null;
        parameterCount = 0;
        returnParametersToPool = false;

        int providedParametersLength = providedParameters.ValueKind == JsonValueKind.Array ? providedParameters.GetArrayLength() : 0;
        int missingParamsCount = CountMissingJsonElementParameters(expectedParameters, providedParameters, providedParametersLength, out int missingRequiredParameterIndex);

        JsonRpcErrorResponse? validationError = ValidateMissingParameters(
            expectedParameters,
            methodName,
            in requestId,
            providedParametersLength,
            missingRequiredParameterIndex,
            ref missingParamsCount);
        if (validationError is not null)
        {
            return validationError;
        }

        parameters = DeserializeParameters(
            expectedParameters,
            providedParametersLength,
            providedParameters,
            providedParametersUtf8,
            missingParamsCount,
            out parameterCount,
            out returnParametersToPool);
        return null;
    }

    private static int CountMissingJsonElementParameters(
        ExpectedParameter[] expectedParameters,
        JsonElement providedParameters,
        int providedParametersLength,
        out int missingRequiredParameterIndex)
    {
        int missingParamsCount = expectedParameters.Length - providedParametersLength;
        int initialMissingParamsCount = missingParamsCount;
        missingRequiredParameterIndex = -1;

        if (providedParametersLength > 0)
        {
            int index = 0;
            foreach (JsonElement item in providedParameters.EnumerateArray())
            {
                bool isMissing = IsMissingParameterMarker(item);
                missingParamsCount = isMissing ? missingParamsCount + 1 : initialMissingParamsCount;
                if (isMissing)
                {
                    TrackMissingRequiredParameter(expectedParameters, index, ref missingRequiredParameterIndex);
                }

                index++;
            }
        }

        return missingParamsCount;
    }

    private void LogRequest(
        string methodName,
        ExpectedParameter[] expectedParameters,
        bool useUtf8Parameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        JsonElement providedParameters)
    {
        if (!_logger.IsTrace)
        {
            return;
        }

        if (useUtf8Parameters)
        {
            LogRequest(methodName, providedParametersUtf8, expectedParameters);
        }
        else
        {
            LogRequest(methodName, providedParameters, expectedParameters);
        }
    }

    private static bool HasUnexpectedZeroParameterArray(
        bool useUtf8Parameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        JsonElement providedParameters) =>
        useUtf8Parameters
            ? HasAnyUtf8Parameter(providedParametersUtf8)
            : providedParameters.ValueKind == JsonValueKind.Array && providedParameters.GetArrayLength() != 0;

    private static bool HasAnyUtf8Parameter(ReadOnlyMemory<byte> providedParametersUtf8)
    {
        JsonReaderState readerState = default;
        int offset = 0;
        bool started = false;
        return JsonRpcArrayReader.TryReadNextItemRange(
            providedParametersUtf8,
            ref offset,
            ref readerState,
            ref started,
            out _,
            out _);
    }

    private JsonRpcErrorResponse? ValidateMissingParameters(
        ExpectedParameter[] expectedParameters,
        string methodName,
        in JsonRpcId requestId,
        int providedParametersLength,
        int missingRequiredParameterIndex,
        ref int missingParamsCount)
    {
        // The JSON element deserializer walks every provided element against expectedParameters, so an
        // over-long request has to be rejected here rather than indexing past the end.
        if (providedParametersLength > expectedParameters.Length || missingParamsCount < 0)
        {
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, in requestId);
        }

        if (missingRequiredParameterIndex >= 0)
        {
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams,
                $"missing value for required argument {missingRequiredParameterIndex}", null, in requestId);
        }

        int explicitNullableParamsCount = 0;

        for (int i = 0; i < missingParamsCount; i++)
        {
            int parameterIndex = expectedParameters.Length - missingParamsCount + i;

            // Preserve compatibility for calls that pass trailing nullable defaults as null or "".
            bool isExplicit = providedParametersLength >= parameterIndex + 1;
            if (expectedParameters[parameterIndex].IsNullable && isExplicit)
            {
                explicitNullableParamsCount += 1;
            }

            if (RequiresExplicitValue(in expectedParameters[parameterIndex]))
            {
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams,
                    $"missing value for required argument {parameterIndex}", null, in requestId);
            }
        }

        missingParamsCount -= explicitNullableParamsCount;
        return null;
    }

    private static bool CanDeserializeParametersFromUtf8(JsonRpcRequest request, ExpectedParameter[] expectedParameters)
    {
        if (request.ParamsUtf8.IsEmpty || request.ParamsKind != JsonValueKind.Array)
        {
            return false;
        }

        for (int i = 0; i < expectedParameters.Length; i++)
        {
            if (expectedParameters[i].Kind == ParameterKind.JsonElement)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMissingParameterMarker(in Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Null
        || (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ReadOnlySpan<byte>.Empty));

    private static bool IsMissingParameterMarker(JsonElement item) =>
        item.ValueKind == JsonValueKind.Null
        || (item.ValueKind == JsonValueKind.String && item.ValueEquals(ReadOnlySpan<byte>.Empty));

    private static bool RequiresExplicitValue(in ExpectedParameter parameter) =>
        !parameter.IsOptional && !parameter.IsNullable;

    /// <summary>
    /// Records <paramref name="index"/> as the first argument where a missing-argument marker landed on a
    /// parameter that requires an explicit value, leaving an already recorded index untouched.
    /// </summary>
    private static void TrackMissingRequiredParameter(ExpectedParameter[] expectedParameters, int index, ref int missingRequiredParameterIndex)
    {
        if (missingRequiredParameterIndex < 0
            && index < expectedParameters.Length
            && RequiresExplicitValue(in expectedParameters[index]))
        {
            missingRequiredParameterIndex = index;
        }
    }

    private JsonRpcErrorResponse HandleInvocationException(Exception ex, string methodName, JsonRpcRequest request, Action? returnAction)
    {
        return ex switch
        {
            // Must precede the ArgumentException arm: ResourceNotFoundException derives from it, and answering
            // "invalid params" (or a generic internal error) for history the node does not hold reads as a
            // retry-forever signal to indexers. EIP-4444 defines the accurate code.
            ResourceNotFoundException or TargetInvocationException { InnerException: ResourceNotFoundException } =>
                KeepTrace(ex, GetErrorResponse(methodName, ErrorCodes.PrunedHistoryUnavailable,
                    ErrorMessages.PrunedHistoryUnavailable, GetExceptionText(ex), in request.IdRef, returnAction)),

            TargetParameterCountException or ArgumentException =>
                KeepTrace(ex, GetErrorResponse(methodName, ErrorCodes.InvalidParams, ex.Message, GetExceptionText(ex), in request.IdRef, returnAction)),

            JsonException or TargetInvocationException and { InnerException: JsonException } =>
                KeepTrace(ex, GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", GetExceptionText(ex), in request.IdRef, returnAction)),

            OperationCanceledException or { InnerException: OperationCanceledException } =>
                GetErrorResponse(methodName, ErrorCodes.Timeout,
                    $"{methodName} request was canceled due to enabled timeout.", null, in request.IdRef, returnAction),

            // suppressWarning doubles as the overload-shedding marker: GetErrorResponse counts
            // suppressed LimitExceeded/ModuleTimeout responses in Metrics.JsonRpcOverloadRejections.
            LimitExceededException or ConcurrencyLimitReachedException
                or { InnerException: LimitExceededException }
                or { InnerException: ConcurrencyLimitReachedException } =>
                GetErrorResponse(methodName, ErrorCodes.LimitExceeded, "Too many requests", null, in request.IdRef, returnAction, suppressWarning: true),

            InsufficientBalanceException or { InnerException: InsufficientBalanceException } =>
                KeepTrace(ex, GetErrorResponse(methodName, ErrorCodes.InvalidInput, GetInsufficientBalanceMessage(ex), GetExceptionText(ex), in request.IdRef, returnAction)),

            InvalidTransactionException or { InnerException: InvalidTransactionException } when (ex as InvalidTransactionException ?? ex.InnerException as InvalidTransactionException) is { Reason.ErrorDescription: var description } =>
                GetErrorResponse(methodName, ErrorCodes.Default, description, null, in request.IdRef, returnAction),

            InvalidBlockException or { InnerException: InvalidBlockException } =>
                GetErrorResponse(methodName, ErrorCodes.Default, ex.Message, null, in request.IdRef, returnAction),

            MissingTrieNodeException e =>
                HandleMissingTrieNode(e, methodName, request, returnAction),

            TargetInvocationException { InnerException: MissingTrieNodeException e } =>
                HandleMissingTrieNode(e, methodName, request, returnAction),

            _ => HandleException(ex, methodName, request, returnAction)
        };

        // GetExceptionText drops the stack trace from error.data on purpose, so the arms that answer with it and log
        // nothing else of their own would leave the trace recoverable nowhere - a diagnosis regression, not part of
        // the leak fix. Debug, because those arms are the caller's fault (#13156). HandleException logs at Error for
        // itself and is deliberately not routed through here: a second line would be duplicate, not detail.
        JsonRpcErrorResponse KeepTrace(Exception ex, JsonRpcErrorResponse response)
        {
            _logger.DebugError($"Exception during {methodName} execution", ex);
            return response;
        }

        JsonRpcErrorResponse HandleException(Exception ex, string methodName, JsonRpcRequest request, Action? returnAction)
        {
            if (_logger.IsError) _logger.Error($"Error during method execution, request: {DescribeForErrorLog(request, ex)}", ex);
            return GetErrorResponse(methodName, ErrorCodes.InternalError, "Internal error", GetExceptionText(ex), in request.IdRef, returnAction);
        }

        static string GetInsufficientBalanceMessage(Exception ex) =>
            (ex as InsufficientBalanceException ?? ex.InnerException as InsufficientBalanceException)!.Message;

        JsonRpcErrorResponse HandleMissingTrieNode(MissingTrieNodeException ex, string methodName, JsonRpcRequest request, Action? returnAction)
        {
            // HasStateForBlock only checks the state root; subtree nodes can still be pruned out
            // after a successful guard. Surface as -32000 (Geth wire parity) and warn so operators
            // can investigate whether it's a legitimate pruning gap or a deeper issue.
            if (_logger.IsWarn) _logger.Warn($"Missing trie node during {methodName}: {ex.Message}");
            // The Warn above carries the message but not the exception, so the trace still needs KeepTrace.
            return KeepTrace(ex, GetErrorResponse(methodName, ErrorCodes.ResourceNotFound, ex.Message, GetExceptionText(ex), in request.IdRef, returnAction));
        }
    }

    /// <summary>Renders an exception chain for <c>error.data</c> without exposing its stack trace.</summary>
    /// <remarks>
    /// <c>error.data</c> is returned to unauthenticated callers, and <see cref="Exception.ToString"/> embeds the
    /// stack trace, which these builds render with the build machine's absolute source paths and with the internal
    /// call graph. Only the chain's types and messages are reported here; the full trace is written to the node log.
    /// </remarks>
    private static string GetExceptionText(Exception ex)
    {
        StringBuilder text = new();
        Exception? current = (ex as TargetInvocationException)?.InnerException ?? ex;
        for (int depth = 0; current is not null && depth < MaxReportedExceptionChainDepth; current = current.InnerException, depth++)
        {
            if (text.Length != 0) text.Append(" ---> ");
            text.Append(current.GetType()).Append(": ").Append(current.Message);
        }

        return text.ToString();
    }

    private void LogRequest(string methodName, JsonElement providedParameters, ExpectedParameter[] expectedParameters)
    {
        if (_methodsLoggingFiltering.Contains(methodName))
        {
            return;
        }

        StringBuilder builder = new($"Executing JSON RPC call {methodName} with params [");
        int paramsLength = 0;
        int paramsCount = 0;

        if (providedParameters.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement param in providedParameters.EnumerateArray())
            {
                string? parameter = IsPassphraseParameter(paramsCount, expectedParameters)
                    ? "{passphrase}"
                    : param.GetRawText();
                if (!AppendLogParameter(builder, parameter, ref paramsLength, paramsCount)) break;
                paramsCount++;
            }
        }

        _logger.Trace(builder.Append(']').ToString());
    }

    private void LogRequest(string methodName, ReadOnlyMemory<byte> providedParameters, ExpectedParameter[] expectedParameters)
    {
        if (_methodsLoggingFiltering.Contains(methodName))
        {
            return;
        }

        StringBuilder builder = new($"Executing JSON RPC call {methodName} with params [");
        int paramsLength = 0;
        int paramsCount = 0;
        JsonReaderState readerState = default;
        int offset = 0;
        bool started = false;
        while (JsonRpcArrayReader.TryReadNextItem(providedParameters, ref offset, ref readerState, ref started, out ReadOnlyMemory<byte> param))
        {
            string parameter = IsPassphraseParameter(paramsCount, expectedParameters)
                ? "{passphrase}"
                : Encoding.UTF8.GetString(param.Span);
            if (!AppendLogParameter(builder, parameter, ref paramsLength, paramsCount)) break;
            paramsCount++;
        }

        _logger.Trace(builder.Append(']').ToString());
    }

    private bool AppendLogParameter(StringBuilder builder, string? parameter, ref int paramsLength, int paramsCount)
    {
        const string separator = ", ";
        if (paramsLength > _maxLoggedRequestParametersCharacters)
        {
            int toRemove = paramsLength - _maxLoggedRequestParametersCharacters;
            builder.Remove(builder.Length - toRemove, toRemove);
            builder.Append("...");
            return false;
        }

        if (paramsCount != 0)
        {
            builder.Append(separator);
            paramsLength += separator.Length;
        }

        builder.Append(parameter);
        paramsLength += parameter?.Length ?? 0;
        return true;
    }

    private static bool IsPassphraseParameter(int paramsCount, ExpectedParameter[] expectedParameters) =>
        (uint)paramsCount < (uint)expectedParameters.Length && expectedParameters[paramsCount].Info?.Name == "passphrase";

    private static string GetParamsForLog(JsonRpcRequest request)
    {
        if (request.Params.ValueKind != JsonValueKind.Undefined)
        {
            return string.Join(", ", request.Params);
        }

        return request.ParamsUtf8.IsEmpty
            ? string.Empty
            : Encoding.UTF8.GetString(request.ParamsUtf8.Span);
    }

    private static object? DeserializeParameter(JsonElement providedParameter, ExpectedParameter expectedParameter, ReadOnlyMemory<byte> providedParameterUtf8)
    {
        if (providedParameter.ValueKind == JsonValueKind.Null || (providedParameter.ValueKind == JsonValueKind.String && providedParameter.ValueEquals(ReadOnlySpan<byte>.Empty)))
        {
            return providedParameter.ValueKind == JsonValueKind.Null && expectedParameter.IsNullable
                ? null
                : expectedParameter.DefaultValue;
        }

        if (expectedParameter.Kind == ParameterKind.String)
        {
            return providedParameter.ValueKind == JsonValueKind.String
                ? providedParameter.GetString()
                : providedParameter.GetRawText();
        }

        if (expectedParameter.Kind == ParameterKind.JsonRpcParam)
        {
            IJsonRpcParam jsonRpcParam = expectedParameter.CreateRpcParam();
            jsonRpcParam!.ReadJson(providedParameter, EthereumJsonSerializer.JsonRpcRequestOptions);
            return jsonRpcParam;
        }

        return expectedParameter.Kind != ParameterKind.JsonElement && !providedParameterUtf8.IsEmpty
            ? DeserializeTypedParameter(providedParameter, expectedParameter, providedParameterUtf8)
            : DeserializeTypedParameter(providedParameter, expectedParameter);
    }

    private static object? DeserializeParameter(ref Utf8JsonReader reader, ExpectedParameter expectedParameter)
    {
        if (reader.TokenType == JsonTokenType.Null || (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ReadOnlySpan<byte>.Empty)))
        {
            return reader.TokenType == JsonTokenType.Null && expectedParameter.IsNullable
                ? null
                : expectedParameter.DefaultValue;
        }

        if (expectedParameter.Kind == ParameterKind.String)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString();
            }

            using JsonDocument jsonDocument = JsonDocument.ParseValue(ref reader);
            return jsonDocument.RootElement.GetRawText();
        }

        if (expectedParameter.Kind == ParameterKind.JsonRpcParam)
        {
            using JsonDocument jsonDocument = JsonDocument.ParseValue(ref reader);
            IJsonRpcParam jsonRpcParam = expectedParameter.CreateRpcParam();
            jsonRpcParam.ReadJson(jsonDocument.RootElement, EthereumJsonSerializer.JsonRpcRequestOptions);
            return jsonRpcParam;
        }

        if (reader.TokenType == JsonTokenType.String && expectedParameter.ReparseString)
        {
            return DeserializeReparsedString(reader.GetString(), expectedParameter);
        }

        return DeserializeTypedParameter(ref reader, expectedParameter);
    }

    private static object? DeserializeTypedParameter(JsonElement providedParameter, ExpectedParameter expectedParameter, ReadOnlyMemory<byte> providedParameterUtf8 = default)
    {
        Type paramType = expectedParameter.ParameterType;
        if (providedParameter.ValueKind == JsonValueKind.String && expectedParameter.ReparseString)
        {
            return DeserializeReparsedString(providedParameter.GetString(), expectedParameter);
        }

        JsonTypeInfo? typeInfo = expectedParameter.TypeInfo;
        if (providedParameterUtf8.IsEmpty)
        {
            return typeInfo is not null
                ? providedParameter.Deserialize(typeInfo)
                : providedParameter.Deserialize(paramType, EthereumJsonSerializer.JsonRpcRequestOptions);
        }

        return DeserializeTypedParameter(providedParameterUtf8.Span, expectedParameter);
    }

    private static object? DeserializeTypedParameter(ReadOnlySpan<byte> providedParameterUtf8, ExpectedParameter expectedParameter)
    {
        JsonTypeInfo? typeInfo = expectedParameter.TypeInfo;
        return typeInfo is not null
            ? JsonSerializer.Deserialize(providedParameterUtf8, typeInfo)
            : JsonSerializer.Deserialize(providedParameterUtf8, expectedParameter.ParameterType, EthereumJsonSerializer.JsonRpcRequestOptions);
    }

    private static object? DeserializeTypedParameter(ref Utf8JsonReader reader, ExpectedParameter expectedParameter)
    {
        JsonTypeInfo? typeInfo = expectedParameter.TypeInfo;
        return typeInfo is not null
            ? JsonSerializer.Deserialize(ref reader, typeInfo)
            : JsonSerializer.Deserialize(ref reader, expectedParameter.ParameterType, EthereumJsonSerializer.JsonRpcRequestOptions);
    }

    private static object? DeserializeReparsedString(string? json, ExpectedParameter expectedParameter)
    {
        if (json is null)
        {
            return null;
        }

        JsonTypeInfo? typeInfo = expectedParameter.HasParameterConverter ? expectedParameter.TypeInfo : null;
        return typeInfo is not null
            ? JsonSerializer.Deserialize(json, typeInfo)
            : JsonSerializer.Deserialize(json, expectedParameter.ParameterType, EthereumJsonSerializer.JsonRpcRequestOptions);
    }

    private static object?[] DeserializeParameters(
        ExpectedParameter[] expectedParameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        out int providedParametersLength,
        out int missingParamsCount,
        out int missingRequiredParameterIndex,
        out ExceptionDispatchInfo? parameterDeserializationException,
        out bool returnParametersToPool)
    {
        providedParametersLength = 0;
        missingParamsCount = 0;
        missingRequiredParameterIndex = -1;
        parameterDeserializationException = null;
        returnParametersToPool = false;

        object?[] executionParameters = RentParameterArray(expectedParameters.Length, out returnParametersToPool);

        Utf8JsonReader reader = new(providedParametersUtf8.Span, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            ThrowExpectedJsonArray();
        }

        if (!reader.Read())
        {
            ThrowIncompleteJsonArray();
        }

        int trailingMissingParamsCount = 0;
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            if (providedParametersLength == expectedParameters.Length)
            {
                missingParamsCount = -1;
                providedParametersLength++;
                return executionParameters;
            }

            bool isMissing = IsMissingParameterMarker(in reader);
            trailingMissingParamsCount = isMissing ? trailingMissingParamsCount + 1 : 0;
            if (isMissing)
            {
                TrackMissingRequiredParameter(expectedParameters, providedParametersLength, ref missingRequiredParameterIndex);
            }

            Utf8JsonReader parameterReader = reader;
            try
            {
                executionParameters[providedParametersLength] = DeserializeParameter(ref parameterReader, expectedParameters[providedParametersLength]);
                reader = parameterReader;
            }
            catch (Exception e)
            {
                parameterDeserializationException ??= ExceptionDispatchInfo.Capture(e);
                reader.Skip();
            }

            providedParametersLength++;

            if (!reader.Read())
            {
                ThrowIncompleteJsonArray();
            }
        }

        missingParamsCount = expectedParameters.Length - providedParametersLength + trailingMissingParamsCount;
        return executionParameters;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowExpectedJsonArray() =>
            throw new JsonException("Expected JSON array.");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowIncompleteJsonArray() =>
            throw new JsonException("Incomplete JSON array.");
    }

    private static object?[] DeserializeParameters(
        ExpectedParameter[] expectedParameters,
        int providedParametersLength,
        JsonElement providedParameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        int missingParamsCount,
        out int parameterCount,
        out bool returnParametersToPool)
    {
        int totalLength = Math.Min(expectedParameters.Length, providedParametersLength + missingParamsCount);
        parameterCount = totalLength;
        returnParametersToPool = false;
        if (totalLength == 0) return [];

        object?[] executionParameters = RentParameterArray(totalLength, out returnParametersToPool);

        int i = 0;

        if (providedParametersLength > 0)
        {
            JsonElement.ArrayEnumerator enumerator = providedParameters.EnumerateArray();
            bool useUtf8Parameters = !providedParametersUtf8.IsEmpty && providedParameters.ValueKind == JsonValueKind.Array;
            JsonReaderState readerState = default;
            int offset = 0;
            bool started = false;
            while (enumerator.MoveNext())
            {
                ExpectedParameter expectedParameter = expectedParameters[i];
                ReadOnlyMemory<byte> providedParameterUtf8 = default;
                if (useUtf8Parameters && !JsonRpcArrayReader.TryReadNextItem(providedParametersUtf8, ref offset, ref readerState, ref started, out providedParameterUtf8))
                {
                    ThrowMissingParameterBytes();
                }

                object? parameter = DeserializeParameter(enumerator.Current, expectedParameter, providedParameterUtf8);
                executionParameters[i] = parameter;
                i++;
            }
        }

        FillDefaultParameters(expectedParameters, executionParameters, providedParametersLength, totalLength);
        return executionParameters;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowMissingParameterBytes() =>
            throw new JsonException("Missing JSON-RPC parameter bytes.");
    }

    private static string? GetSafePublicMessage(Exception e)
    {
        for (Exception? ex = e; ex is not null; ex = ex.InnerException)
        {
            if (ex is IExceptionWithSafePublicMessage)
                return ex.Message;
        }
        return null;
    }

    private static void FillDefaultParameters(ExpectedParameter[] expected, object?[] actual, int start, int count)
    {
        for (int i = start; i < count; i++) actual[i] = expected[i].DefaultValue;
    }

    private static object?[] RentParameterArray(int length, out bool returnToPool)
    {
        returnToPool = length <= MaxPooledParameterCount;
        return returnToPool ? ArrayPool<object?>.Shared.Rent(length) : new object?[length];
    }

    public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, string? methodName = null)
        => GetErrorResponse(errorCode, errorMessage, in JsonRpcId.Null, methodName);

    public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, in JsonRpcId id, string? methodName = null) =>
        GetErrorResponse(methodName ?? string.Empty, errorCode, errorMessage, null, in id);

    private JsonRpcErrorResponse GetErrorResponse(
        string methodName,
        int errorCode,
        string? errorMessage,
        object? errorData,
        in JsonRpcId id,
        Action? disposableAction = null,
        bool suppressWarning = false)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending error response, method: {(string.IsNullOrEmpty(methodName) ? "none" : methodName)}, id: {id}, errorType: {errorCode}, message: {errorMessage}, errorData: {errorData}");
        // Counted here, at the funnel every error response passes through: concurrency-cap
        // rejections reach this point along two distinct paths (module rental before invocation,
        // and the override-environment cap during invocation), and their warnings are suppressed
        // by design — without a counter operators cannot see that callers are being shed.
        // suppressWarning scopes the count to exactly those shedding sites: batch-size and
        // response-body caps also produce LimitExceeded but keep their warnings.
        if (suppressWarning && errorCode is ErrorCodes.LimitExceeded or ErrorCodes.ModuleTimeout)
        {
            Metrics.IncrementJsonRpcOverloadRejections();
        }
        JsonRpcErrorResponse response = new(in id, disposableAction)
        {
            Error = new Error
            {
                Code = errorCode,
                Message = errorMessage,
                Data = errorData,
                SuppressWarning = suppressWarning
            }
        };

        return response;
    }

    private (int? ErrorType, string? ErrorMessage, string MethodName, ResolvedMethodInfo? Method, bool OperatorActionable) Validate(JsonRpcRequest? rpcRequest, JsonRpcContext context)
    {
        if (rpcRequest is null)
        {
            return (ErrorCodes.InvalidRequest, "Invalid request", string.Empty, null, false);
        }

        string methodName = rpcRequest.Method;
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return (ErrorCodes.InvalidRequest, "Method is required", methodName, null, false);
        }

        string trimmedMethodName = methodName.Trim();

        ModuleResolution result = _rpcModuleProvider.Check(trimmedMethodName, context, out string? module, out ResolvedMethodInfo? method);
        if (result == ModuleResolution.Enabled)
        {
            return (null, null, trimmedMethodName, method, false);
        }

        (int? errorType, string errorMessage, bool operatorActionable) = GetErrorResult(trimmedMethodName, context, result, module);
        return (errorType, errorMessage, methodName, null, operatorActionable);

        // OperatorActionable is decided here, at the only place that knows *why* the request failed. A namespace
        // that is disabled for this URL or this endpoint is a fact about the node's configuration, not about the
        // request, and its message tells the operator how to fix it - so it must not be demoted with the rest of
        // the -32600 traffic. Unknown methods and failed authentication are genuine caller faults.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (int? ErrorType, string ErrorMessage, bool OperatorActionable) GetErrorResult(string methodName, JsonRpcContext context, ModuleResolution result, string module) => result switch
        {
            ModuleResolution.Unknown => (ErrorCodes.MethodNotFound, ErrorMessages.MethodNotFound(methodName), false),
            ModuleResolution.Disabled => (ErrorCodes.InvalidRequest,
                $"The method '{methodName}' is found but the namespace '{module}' is disabled for {context.Url?.ToString() ?? "n/a"}. Consider adding the namespace '{module}' to JsonRpc.AdditionalRpcUrls for an additional URL, or to JsonRpc.EnabledModules for the default URL.", true),
            ModuleResolution.EndpointDisabled => (ErrorCodes.InvalidRequest,
                $"The method '{methodName}' is found in namespace '{module}' for {context.Url?.ToString() ?? "n/a"}' but is disabled for {context.RpcEndpoint}.", true),
            ModuleResolution.NotAuthenticated => (ErrorCodes.InvalidRequest, $"The method '{methodName}' must be authenticated.", false),
            _ => (null, null, false)
        };
    }
}
