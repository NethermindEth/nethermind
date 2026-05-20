// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
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

    private readonly ILogger _logger = logManager.GetClassLogger<JsonRpcService>();
    private readonly IRpcModuleProvider _rpcModuleProvider = rpcModuleProvider;
    private readonly HashSet<string> _methodsLoggingFiltering = (jsonRpcConfig.MethodsLoggingFiltering ?? []).ToHashSet();
    private readonly int _maxLoggedRequestParametersCharacters = jsonRpcConfig.MaxLoggedRequestParametersCharacters ?? int.MaxValue;
    private static readonly ConcurrentDictionary<Type, TaskResultAccessor> _taskResultAccessors = new();
    private static readonly ConcurrentDictionary<Type, SuccessResponseAccessor> _successResponseAccessors = new();
    private static readonly MethodInfo _readTaskResultMethod = typeof(JsonRpcService).GetMethod(nameof(ReadTaskResult), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo _getTypedSuccessResponseMethod = typeof(JsonRpcService).GetMethod(nameof(GetTypedSuccessResponse), BindingFlags.NonPublic | BindingFlags.Static)!;

    private delegate IResultWrapper? TaskResultAccessor(Task task);
    private delegate JsonRpcResponse SuccessResponseAccessor(IResultWrapper wrapper, string methodName, JsonRpcId id, Action? returnAction);

    public ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
    {
        long serviceStartTimestamp = Stopwatch.GetTimestamp();
        long boundaryStartTimestamp = rpcRequest.BoundaryStartTimestamp != 0
            ? rpcRequest.BoundaryStartTimestamp
            : serviceStartTimestamp;
        (int? errorCode, string? errorMessage, string methodName, ResolvedMethodInfo? method) = Validate(rpcRequest, context);
        if (errorCode.HasValue)
        {
            if (_logger.IsDebug) _logger.Debug($"Validation error when handling request: {rpcRequest}");
            JsonRpcErrorResponse response = GetErrorResponse(methodName, errorCode.Value, errorMessage, null, rpcRequest.Id);
            response.BoundaryTimings = GetPreMethodTimings(rpcRequest, boundaryStartTimestamp, Stopwatch.GetTimestamp());
            return ValueTask.FromResult<JsonRpcResponse>(response);
        }

        try
        {
            ValueTask<JsonRpcResponse> responseTask = ExecuteRequestAsync(rpcRequest, methodName, method!, context, boundaryStartTimestamp);
            return responseTask.IsCompletedSuccessfully
                ? responseTask
                : AwaitRequestAsync(responseTask, rpcRequest, boundaryStartTimestamp);
        }
        catch (Exception ex)
        {
            JsonRpcErrorResponse response = ReturnErrorResponse(rpcRequest, ex);
            response.BoundaryTimings = GetPreMethodTimings(rpcRequest, boundaryStartTimestamp, Stopwatch.GetTimestamp());
            return ValueTask.FromResult<JsonRpcResponse>(response);
        }

        async ValueTask<JsonRpcResponse> AwaitRequestAsync(ValueTask<JsonRpcResponse> responseTask, JsonRpcRequest rpcRequest, long boundaryStartTimestamp)
        {
            try
            {
                return await responseTask;
            }
            catch (Exception ex)
            {
                JsonRpcErrorResponse response = ReturnErrorResponse(rpcRequest, ex);
                response.BoundaryTimings = GetPreMethodTimings(rpcRequest, boundaryStartTimestamp, Stopwatch.GetTimestamp());
                return response;
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

        (int errorCode, string errorText) = ex switch
        {
            LimitExceededException or ConcurrencyLimitReachedException => (ErrorCodes.LimitExceeded, "Too many requests"),
            ModuleRentalTimeoutException => (ErrorCodes.ModuleTimeout, "Timeout"),
            _ => (ErrorCodes.InternalError, "Internal error"),
        };

        if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
        return GetErrorResponse(rpcRequest.Method, errorCode, errorText, ex.ToString(), rpcRequest.Id);
    }

    private ValueTask<JsonRpcResponse> ExecuteRequestAsync(JsonRpcRequest rpcRequest, string methodName, ResolvedMethodInfo method, JsonRpcContext context, long boundaryStartTimestamp) =>
        ExecuteAsync(rpcRequest, methodName, method, context, boundaryStartTimestamp);

    private async ValueTask<JsonRpcResponse> ExecuteAsync(JsonRpcRequest request, string methodName, ResolvedMethodInfo method, JsonRpcContext context, long boundaryStartTimestamp)
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
            value.BoundaryTimings = GetPreMethodTimings(request, boundaryStartTimestamp, Stopwatch.GetTimestamp());
            return value;
        }

        IRpcModule rpcModule = await _rpcModuleProvider.Rent(method);
        if (rpcModule is IContextAwareRpcModule contextAwareModule)
        {
            contextAwareModule.Context = context;
        }
        bool returnImmediately = methodName != GetLogsMethodName;
        Action? returnAction = returnImmediately ? null : () => _rpcModuleProvider.Return(method, rpcModule);
        IResultWrapper? resultWrapper = null;
        long methodStartTimestamp = Stopwatch.GetTimestamp();
        long methodEndTimestamp = methodStartTimestamp;
        try
        {
            // Execute method
            object? invocationResult;
            try
            {
                invocationResult = parameterCount switch
                {
                    0 when method.DirectNoParameterInvoker is { } directInvoker => directInvoker(rpcModule),
                    > 0 when method.DirectParameterInvoker is { } directInvoker => directInvoker(rpcModule, parameters!),
                    _ => method.Invoker.Invoke(rpcModule, parameters.AsSpan(0, parameterCount)),
                };
            }
            finally
            {
                ReturnParameters(parameters, returnParametersToPool);
            }

            switch (invocationResult)
            {
                case IResultWrapper wrapper:
                    methodEndTimestamp = Stopwatch.GetTimestamp();
                    resultWrapper = wrapper;
                    break;
                case Task task:
                    await task;
                    methodEndTimestamp = Stopwatch.GetTimestamp();
                    resultWrapper = GetTaskResult(task);
                    break;
                default:
                    methodEndTimestamp = Stopwatch.GetTimestamp();
                    break;
            }
        }
        catch (Exception ex)
        {
            methodEndTimestamp = Stopwatch.GetTimestamp();
            JsonRpcErrorResponse errorResponse = HandleInvocationException(ex, methodName, request, returnAction);
            SetBoundaryTimings(errorResponse, request, boundaryStartTimestamp, methodStartTimestamp, methodEndTimestamp);
            return errorResponse;
        }
        finally
        {
            if (returnImmediately)
            {
                _rpcModuleProvider.Return(method, rpcModule);
            }
        }

        if (resultWrapper is null)
        {
            JsonRpcResponse missingWrapperResponse = HandleMissingResultWrapper(request, methodName, returnAction);
            SetBoundaryTimings(missingWrapperResponse, request, boundaryStartTimestamp, methodStartTimestamp, methodEndTimestamp);
            return missingWrapperResponse;
        }

        Result result = resultWrapper.Result;
        JsonRpcResponse response = result.ResultType != ResultType.Success
            ? GetErrorResponse(methodName, resultWrapper.ErrorCode, result.Error, resultWrapper.Data, request.Id, returnAction, resultWrapper.IsTemporary)
            : GetSuccessResponse(resultWrapper, methodName, request.Id, returnAction);
        SetBoundaryTimings(response, request, boundaryStartTimestamp, methodStartTimestamp, methodEndTimestamp);
        return response;

        [MethodImpl(MethodImplOptions.NoInlining)]
        JsonRpcResponse HandleMissingResultWrapper(JsonRpcRequest request, string methodName, Action? returnAction)
        {
            string errorMessage = $"Method {methodName} execution result does not implement IResultWrapper";
            if (_logger.IsError) _logger.Error(errorMessage);
            return GetErrorResponse(methodName, ErrorCodes.InternalError, errorMessage, null, request.Id, returnAction);
        }
    }

    private static void ReturnParameters(object?[]? parameters, bool returnToPool)
    {
        if (returnToPool && parameters is not null)
        {
            ArrayPool<object?>.Shared.Return(parameters, clearArray: true);
        }
    }

    private static RpcBoundaryTimings GetPreMethodTimings(
        JsonRpcRequest request,
        long boundaryStartTimestamp,
        long boundaryEndTimestamp) =>
        RpcBoundaryTimings.PreMethodOnly(
            boundaryStartTimestamp,
            boundaryEndTimestamp);

    private static void SetBoundaryTimings(
        JsonRpcResponse response,
        JsonRpcRequest request,
        long boundaryStartTimestamp,
        long methodStartTimestamp,
        long methodEndTimestamp) =>
        response.BoundaryTimings = RpcBoundaryTimings.FromTimestamps(
            boundaryStartTimestamp,
            methodStartTimestamp,
            methodEndTimestamp,
            Stopwatch.GetTimestamp());

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

        if (_logger.IsTrace)
        {
            if (useUtf8Parameters)
            {
                LogRequest(methodName, providedParametersUtf8, expectedParameters);
            }
            else
            {
                LogRequest(methodName, providedParameters, expectedParameters);
            }
        }

        if (expectedParameters.Length == 0)
        {
            if (HasUnexpectedZeroParameterArray(useUtf8Parameters, providedParametersUtf8, providedParameters))
            {
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, request.Id);
            }

            parameters = [];
            return null;
        }

        int providedParametersLength = useUtf8Parameters
            ? JsonRpcArrayReader.CountItems(providedParametersUtf8)
            : providedParameters.ValueKind == JsonValueKind.Array ? providedParameters.GetArrayLength() : 0;
        int missingParamsCount = expectedParameters.Length - providedParametersLength;
        int initialMissingParamsCount = missingParamsCount;

        if (providedParametersLength > 0)
        {
            if (useUtf8Parameters)
            {
                JsonReaderState readerState = default;
                int offset = 0;
                bool started = false;
                while (JsonRpcArrayReader.TryReadNextItem(providedParametersUtf8, ref offset, ref readerState, ref started, out ReadOnlyMemory<byte> item))
                {
                    UpdateMissingParamsCount(item, ref missingParamsCount, initialMissingParamsCount);
                }
            }
            else
            {
                foreach (JsonElement item in providedParameters.EnumerateArray())
                {
                    UpdateMissingParamsCount(item, ref missingParamsCount, initialMissingParamsCount);
                }
            }
        }

        JsonRpcErrorResponse? validationError = ValidateMissingParameters(
            expectedParameters,
            methodName,
            request.Id,
            providedParametersLength,
            ref missingParamsCount);
        if (validationError is not null)
        {
            return validationError;
        }

        try
        {
            parameters = useUtf8Parameters
                ? DeserializeParameters(expectedParameters, providedParametersLength, providedParametersUtf8, missingParamsCount, out parameterCount, out returnParametersToPool)
                : DeserializeParameters(expectedParameters, providedParametersLength, providedParameters, providedParametersUtf8, missingParamsCount, out parameterCount, out returnParametersToPool);
        }
        catch (Exception e)
        {
            ReturnParameters(parameters, returnParametersToPool);
            if (_logger.IsWarn) _logger.Warn($"Incorrect JSON RPC parameters when calling {methodName} with params [{GetParamsForLog(request)}] {e}");
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, request.Id);
        }

        return null;
    }

    private static bool HasUnexpectedZeroParameterArray(
        bool useUtf8Parameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        JsonElement providedParameters) =>
        useUtf8Parameters
            ? JsonRpcArrayReader.CountItems(providedParametersUtf8) != 0
            : providedParameters.ValueKind == JsonValueKind.Array && providedParameters.GetArrayLength() != 0;

    private JsonRpcErrorResponse? ValidateMissingParameters(
        ExpectedParameter[] expectedParameters,
        string methodName,
        JsonRpcId requestId,
        int providedParametersLength,
        ref int missingParamsCount)
    {
        int explicitNullableParamsCount = 0;

        if (missingParamsCount != 0)
        {
            bool hasIncorrectParameters = true;
            int firstMissingRequiredIndex = -1;
            if (missingParamsCount > 0)
            {
                hasIncorrectParameters = false;
                for (int i = 0; i < missingParamsCount; i++)
                {
                    int parameterIndex = expectedParameters.Length - missingParamsCount + i;
                    bool nullable = expectedParameters[parameterIndex].IsNullable;

                    // Preserve compatibility for calls that pass trailing nullable defaults as null or "".
                    bool isExplicit = providedParametersLength >= parameterIndex + 1;
                    if (nullable && isExplicit)
                    {
                        explicitNullableParamsCount += 1;
                    }

                    if (!expectedParameters[parameterIndex].IsOptional && !nullable)
                    {
                        hasIncorrectParameters = true;
                        firstMissingRequiredIndex = parameterIndex;
                        break;
                    }
                }
            }

            if (hasIncorrectParameters)
            {
                string message = firstMissingRequiredIndex >= 0
                    ? $"missing value for required argument {firstMissingRequiredIndex}"
                    : "Invalid params";
                return GetErrorResponse(methodName, ErrorCodes.InvalidParams, message, null, requestId);
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

    private static void UpdateMissingParamsCount(ReadOnlyMemory<byte> item, ref int missingParamsCount, int initialMissingParamsCount)
    {
        Utf8JsonReader reader = new(item.Span, isFinalBlock: true, state: default);
        if (!reader.Read())
        {
            ThrowInvalidParameterBytes();
        }

        if (reader.TokenType == JsonTokenType.Null || (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ReadOnlySpan<byte>.Empty)))
        {
            missingParamsCount++;
        }
        else
        {
            missingParamsCount = initialMissingParamsCount;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidParameterBytes() =>
            throw new JsonException("Invalid JSON-RPC parameter bytes.");
    }

    private static void UpdateMissingParamsCount(JsonElement item, ref int missingParamsCount, int initialMissingParamsCount)
    {
        if (item.ValueKind == JsonValueKind.Null || (item.ValueKind == JsonValueKind.String && item.ValueEquals(ReadOnlySpan<byte>.Empty)))
        {
            missingParamsCount++;
        }
        else
        {
            missingParamsCount = initialMissingParamsCount;
        }
    }

    private JsonRpcErrorResponse HandleInvocationException(Exception ex, string methodName, JsonRpcRequest request, Action? returnAction)
    {
        return ex switch
        {
            TargetParameterCountException or ArgumentException =>
                GetErrorResponse(methodName, ErrorCodes.InvalidParams, ex.Message, ex.ToString(), request.Id, returnAction),

            JsonException or TargetInvocationException and { InnerException: JsonException } =>
                GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", GetExceptionText(ex), request.Id, returnAction),

            OperationCanceledException or { InnerException: OperationCanceledException } =>
                GetErrorResponse(methodName, ErrorCodes.Timeout,
                    $"{methodName} request was canceled due to enabled timeout.", null, request.Id, returnAction),

            LimitExceededException or ConcurrencyLimitReachedException
                or { InnerException: LimitExceededException }
                or { InnerException: ConcurrencyLimitReachedException } =>
                GetErrorResponse(methodName, ErrorCodes.LimitExceeded, "Too many requests", null, request.Id, returnAction),

            InsufficientBalanceException or { InnerException: InsufficientBalanceException } =>
                GetErrorResponse(methodName, ErrorCodes.InvalidInput, GetInsufficientBalanceMessage(ex), ex.ToString(), request.Id, returnAction),

            InvalidTransactionException or { InnerException: InvalidTransactionException } when (ex as InvalidTransactionException ?? ex.InnerException as InvalidTransactionException) is { Reason.ErrorDescription: var description } =>
                GetErrorResponse(methodName, ErrorCodes.Default, description, null, request.Id, returnAction),

            InvalidBlockException or { InnerException: InvalidBlockException } =>
                GetErrorResponse(methodName, ErrorCodes.Default, ex.Message, null, request.Id, returnAction),

            MissingTrieNodeException e =>
                HandleMissingTrieNode(e, methodName, request, returnAction),

            TargetInvocationException { InnerException: MissingTrieNodeException e } =>
                HandleMissingTrieNode(e, methodName, request, returnAction),

            _ => HandleException(ex, methodName, request, returnAction)
        };

        JsonRpcErrorResponse HandleException(Exception ex, string methodName, JsonRpcRequest request, Action? returnAction)
        {
            if (_logger.IsError) _logger.Error($"Error during method execution, request: {request}", ex);
            return GetErrorResponse(methodName, ErrorCodes.InternalError, "Internal error", ex.ToString(), request.Id, returnAction);
        }

        static string GetExceptionText(Exception ex) => (ex as TargetInvocationException)?.InnerException?.ToString() ?? ex.ToString();

        static string GetInsufficientBalanceMessage(Exception ex) =>
            (ex as InsufficientBalanceException ?? ex.InnerException as InsufficientBalanceException)!.Message;

        JsonRpcErrorResponse HandleMissingTrieNode(MissingTrieNodeException ex, string methodName, JsonRpcRequest request, Action? returnAction)
        {
            // HasStateForBlock only checks the state root; subtree nodes can still be pruned out
            // after a successful guard. Surface as -32000 (Geth wire parity) and warn so operators
            // can investigate whether it's a legitimate pruning gap or a deeper issue.
            if (_logger.IsWarn) _logger.Warn($"Missing trie node during {methodName}: {ex.Message}");
            return GetErrorResponse(methodName, ErrorCodes.ResourceNotFound, ex.Message, ex.ToString(), request.Id, returnAction);
        }
    }

    private static IResultWrapper? GetTaskResult(Task task) =>
        _taskResultAccessors.GetOrAdd(task.GetType(), static taskType => CreateTaskResultAccessor(taskType))(task);

    private static TaskResultAccessor CreateTaskResultAccessor(Type taskType)
    {
        Type? resultType = GetTaskResultType(taskType);
        if (resultType is null || !resultType.IsAssignableTo(typeof(IResultWrapper)))
        {
            return static _ => null;
        }

        return _readTaskResultMethod.MakeGenericMethod(resultType).CreateDelegate<TaskResultAccessor>();
    }

    private static Type? GetTaskResultType(Type taskType)
    {
        for (Type? type = taskType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static IResultWrapper? ReadTaskResult<TResult>(Task task)
        where TResult : IResultWrapper =>
        ((Task<TResult>)task).Result;

    private static JsonRpcResponse GetSuccessResponse(
        IResultWrapper resultWrapper,
        string methodName,
        JsonRpcId id,
        Action? disposableAction) =>
        _successResponseAccessors.GetOrAdd(resultWrapper.GetType(), static wrapperType => CreateSuccessResponseAccessor(wrapperType))(
            resultWrapper,
            methodName,
            id,
            disposableAction);

    private static SuccessResponseAccessor CreateSuccessResponseAccessor(Type wrapperType)
    {
        Type? resultType = GetResultWrapperDataType(wrapperType);
        if (resultType is null || resultType == typeof(object))
        {
            return static (wrapper, methodName, id, disposableAction) =>
                GetSuccessResponse(methodName, wrapper.Data, id, disposableAction);
        }

        return _getTypedSuccessResponseMethod
            .MakeGenericMethod(resultType)
            .CreateDelegate<SuccessResponseAccessor>();
    }

    private static Type? GetResultWrapperDataType(Type wrapperType)
    {
        for (Type? type = wrapperType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ResultWrapper<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static JsonRpcResponse GetTypedSuccessResponse<T>(
        IResultWrapper wrapper,
        string methodName,
        JsonRpcId id,
        Action? disposableAction)
    {
        if (wrapper is not ResultWrapper<T> typedWrapper)
        {
            return GetSuccessResponse(methodName, wrapper.Data, id, disposableAction);
        }

        return GetSuccessResponse(methodName, typedWrapper.Data, id, disposableAction);
    }

    private void LogRequest(string methodName, JsonElement providedParameters, ExpectedParameter[] expectedParameters)
    {
        if (_logger.IsTrace && !_methodsLoggingFiltering.Contains(methodName))
        {
            StringBuilder builder = new();
            builder.Append("Executing JSON RPC call ");
            builder.Append(methodName);
            builder.Append(" with params [");

            int paramsLength = 0;
            int paramsCount = 0;
            const string separator = ", ";

            if (providedParameters.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement param in providedParameters.EnumerateArray())
                {
                    string? parameter = (uint)paramsCount < (uint)expectedParameters.Length && expectedParameters[paramsCount].Info?.Name == "passphrase"
                        ? "{passphrase}"
                        : param.GetRawText();

                    if (paramsLength > _maxLoggedRequestParametersCharacters)
                    {
                        int toRemove = paramsLength - _maxLoggedRequestParametersCharacters;
                        builder.Remove(builder.Length - toRemove, toRemove);
                        builder.Append("...");
                        break;
                    }

                    if (paramsCount != 0)
                    {
                        builder.Append(separator);
                        paramsLength += separator.Length;
                    }

                    builder.Append(parameter);
                    paramsLength += (parameter?.Length ?? 0);
                    paramsCount++;
                }
            }
            builder.Append(']');
            string log = builder.ToString();
            _logger.Trace(log);
        }
    }

    private void LogRequest(string methodName, ReadOnlyMemory<byte> providedParameters, ExpectedParameter[] expectedParameters)
    {
        if (_logger.IsTrace && !_methodsLoggingFiltering.Contains(methodName))
        {
            StringBuilder builder = new();
            builder.Append("Executing JSON RPC call ");
            builder.Append(methodName);
            builder.Append(" with params [");

            int paramsLength = 0;
            int paramsCount = 0;
            const string separator = ", ";
            JsonReaderState readerState = default;
            int offset = 0;
            bool started = false;
            while (JsonRpcArrayReader.TryReadNextItem(providedParameters, ref offset, ref readerState, ref started, out ReadOnlyMemory<byte> param))
            {
                string parameter = (uint)paramsCount < (uint)expectedParameters.Length && expectedParameters[paramsCount].Info?.Name == "passphrase"
                    ? "{passphrase}"
                    : Encoding.UTF8.GetString(param.Span);

                if (paramsLength > _maxLoggedRequestParametersCharacters)
                {
                    int toRemove = paramsLength - _maxLoggedRequestParametersCharacters;
                    builder.Remove(builder.Length - toRemove, toRemove);
                    builder.Append("...");
                    break;
                }

                if (paramsCount != 0)
                {
                    builder.Append(separator);
                    paramsLength += separator.Length;
                }

                builder.Append(parameter);
                paramsLength += parameter.Length;
                paramsCount++;
            }

            builder.Append(']');
            string log = builder.ToString();
            _logger.Trace(log);
        }
    }

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

        object? executionParam;
        if (expectedParameter.Kind == ParameterKind.String)
        {
            executionParam = providedParameter.ValueKind == JsonValueKind.String ?
                providedParameter.GetString() :
                providedParameter.GetRawText();
        }
        else if (expectedParameter.Kind == ParameterKind.JsonRpcParam)
        {
            IJsonRpcParam jsonRpcParam = expectedParameter.CreateRpcParam();
            jsonRpcParam!.ReadJson(providedParameter, EthereumJsonSerializer.JsonOptions);
            executionParam = jsonRpcParam;
        }
        else if (expectedParameter.Kind != ParameterKind.JsonElement && !providedParameterUtf8.IsEmpty)
        {
            executionParam = DeserializeTypedParameter(providedParameter, expectedParameter, providedParameterUtf8);
        }
        else
        {
            executionParam = DeserializeTypedParameter(providedParameter, expectedParameter);
        }

        return executionParam;
    }

    private static object? DeserializeParameter(ReadOnlyMemory<byte> providedParameterUtf8, ExpectedParameter expectedParameter)
    {
        Utf8JsonReader reader = new(providedParameterUtf8.Span, isFinalBlock: true, state: default);
        if (!reader.Read())
        {
            ThrowInvalidParameterBytes();
        }

        if (reader.TokenType == JsonTokenType.Null || (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(ReadOnlySpan<byte>.Empty)))
        {
            return reader.TokenType == JsonTokenType.Null && expectedParameter.IsNullable
                ? null
                : expectedParameter.DefaultValue;
        }

        if (expectedParameter.Kind == ParameterKind.String)
        {
            return reader.TokenType == JsonTokenType.String
                ? reader.GetString()
                : Encoding.UTF8.GetString(providedParameterUtf8.Span);
        }

        if (expectedParameter.Kind == ParameterKind.JsonRpcParam)
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(providedParameterUtf8);
            IJsonRpcParam jsonRpcParam = expectedParameter.CreateRpcParam();
            jsonRpcParam.ReadJson(jsonDocument.RootElement, EthereumJsonSerializer.JsonOptions);
            return jsonRpcParam;
        }

        if (reader.TokenType == JsonTokenType.String && expectedParameter.ReparseString)
        {
            return JsonSerializer.Deserialize(reader.GetString(), expectedParameter.ParameterType, EthereumJsonSerializer.JsonOptions);
        }

        JsonTypeInfo? typeInfo = expectedParameter.TypeInfo;
        return typeInfo is not null
            ? JsonSerializer.Deserialize(ref reader, typeInfo)
            : JsonSerializer.Deserialize(ref reader, expectedParameter.ParameterType, EthereumJsonSerializer.JsonOptions);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidParameterBytes() =>
            throw new JsonException("Invalid JSON-RPC parameter bytes.");
    }

    private static object? DeserializeTypedParameter(JsonElement providedParameter, ExpectedParameter expectedParameter)
    {
        Type paramType = expectedParameter.ParameterType;
        JsonTypeInfo? typeInfo = expectedParameter.TypeInfo;
        if (providedParameter.ValueKind == JsonValueKind.String)
        {
            return expectedParameter.ReparseString
                ? JsonSerializer.Deserialize(providedParameter.GetString(), paramType, EthereumJsonSerializer.JsonOptions)
                : typeInfo is not null
                    ? providedParameter.Deserialize(typeInfo)
                    : providedParameter.Deserialize(paramType, EthereumJsonSerializer.JsonOptions);
        }

        return typeInfo is not null
            ? providedParameter.Deserialize(typeInfo)
            : providedParameter.Deserialize(paramType, EthereumJsonSerializer.JsonOptions);
    }

    private static object? DeserializeTypedParameter(JsonElement providedParameter, ExpectedParameter expectedParameter, ReadOnlyMemory<byte> providedParameterUtf8)
    {
        Type paramType = expectedParameter.ParameterType;
        if (providedParameter.ValueKind == JsonValueKind.String && expectedParameter.ReparseString)
        {
            return JsonSerializer.Deserialize(providedParameter.GetString(), paramType, EthereumJsonSerializer.JsonOptions);
        }

        Utf8JsonReader reader = new(providedParameterUtf8.Span, isFinalBlock: true, state: default);
        JsonTypeInfo? typeInfo = expectedParameter.TypeInfo;
        return typeInfo is not null
            ? JsonSerializer.Deserialize(ref reader, typeInfo)
            : JsonSerializer.Deserialize(ref reader, paramType, EthereumJsonSerializer.JsonOptions);
    }

    private static object?[] DeserializeParameters(
        ExpectedParameter[] expectedParameters,
        int providedParametersLength,
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

        try
        {
            if (providedParametersLength > 0)
            {
                JsonReaderState readerState = default;
                int offset = 0;
                bool started = false;
                while (JsonRpcArrayReader.TryReadNextItem(providedParametersUtf8, ref offset, ref readerState, ref started, out ReadOnlyMemory<byte> providedParameterUtf8))
                {
                    ExpectedParameter expectedParameter = expectedParameters[i];
                    object? parameter = DeserializeParameter(providedParameterUtf8, expectedParameter);
                    executionParameters[i] = parameter;
                    i++;
                }

                if (i != providedParametersLength)
                {
                    ThrowMismatchedParameterCount();
                }
            }

            for (i = providedParametersLength; i < totalLength; i++)
            {
                executionParameters[i] = expectedParameters[i].DefaultValue;
            }

            return executionParameters;
        }
        catch
        {
            ReturnParameters(executionParameters, returnParametersToPool);
            returnParametersToPool = false;
            throw;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowMismatchedParameterCount() =>
            throw new JsonException("Mismatched JSON-RPC parameter count.");
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

        try
        {
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

            for (i = providedParametersLength; i < totalLength; i++)
            {
                executionParameters[i] = expectedParameters[i].DefaultValue;
            }

            return executionParameters;
        }
        catch
        {
            ReturnParameters(executionParameters, returnParametersToPool);
            returnParametersToPool = false;
            throw;
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowMissingParameterBytes() =>
            throw new JsonException("Missing JSON-RPC parameter bytes.");
    }

    private static object?[] RentParameterArray(int length, out bool returnToPool)
    {
        if (length <= MaxPooledParameterCount)
        {
            returnToPool = true;
            return ArrayPool<object?>.Shared.Rent(length);
        }

        returnToPool = false;
        return new object?[length];
    }

    private static JsonRpcResponse GetSuccessResponse(string methodName, object? result, JsonRpcId id, Action? disposableAction)
    {
        JsonRpcResponse response = new JsonRpcSuccessResponse(disposableAction)
        {
            Result = result,
            Id = id,
            MethodName = methodName
        };

        return response;
    }

    private static JsonRpcResponse GetSuccessResponse<T>(string methodName, T result, JsonRpcId id, Action? disposableAction)
    {
        JsonRpcResponse response = new JsonRpcSuccessResponse(disposableAction)
        {
            Result = result,
            Id = id,
            MethodName = methodName,
            ResultStaticType = typeof(T),
            ResultTypeInfoAccessor = JsonRpcSuccessResponseMetadata<T>.Accessor
        };

        return response;
    }

    public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, JsonRpcId? id = null, string? methodName = null) =>
        GetErrorResponse(methodName ?? string.Empty, errorCode, errorMessage, null, id ?? JsonRpcId.Null);

    private JsonRpcErrorResponse GetErrorResponse(
        string methodName,
        int errorCode,
        string? errorMessage,
        object? errorData,
        JsonRpcId id,
        Action? disposableAction = null,
        bool suppressWarning = false)
    {
        if (_logger.IsDebug) _logger.Debug($"Sending error response, method: {(string.IsNullOrEmpty(methodName) ? "none" : methodName)}, id: {id}, errorType: {errorCode}, message: {errorMessage}, errorData: {errorData}");
        JsonRpcErrorResponse response = new(disposableAction)
        {
            Error = new Error
            {
                Code = errorCode,
                Message = errorMessage,
                Data = errorData,
                SuppressWarning = suppressWarning
            },
            Id = id,
            MethodName = methodName
        };

        return response;
    }

    private (int? ErrorType, string? ErrorMessage, string MethodName, ResolvedMethodInfo? Method) Validate(JsonRpcRequest? rpcRequest, JsonRpcContext context)
    {
        if (rpcRequest is null)
        {
            return (ErrorCodes.InvalidRequest, "Invalid request", string.Empty, null);
        }

        string methodName = rpcRequest.Method;
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return (ErrorCodes.InvalidRequest, "Method is required", methodName, null);
        }

        string trimmedMethodName = methodName.Trim();

        ModuleResolution result = _rpcModuleProvider.Check(trimmedMethodName, context, out string? module, out ResolvedMethodInfo? method);
        if (result == ModuleResolution.Enabled)
        {
            return (null, null, trimmedMethodName, method);
        }

        (int? errorType, string errorMessage) = GetErrorResult(trimmedMethodName, context, result, module);
        return (errorType, errorMessage, methodName, null);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (int? ErrorType, string ErrorMessage) GetErrorResult(string methodName, JsonRpcContext context, ModuleResolution result, string module) => result switch
        {
            ModuleResolution.Unknown => (ErrorCodes.MethodNotFound, $"The method '{methodName}' is not supported."),
            ModuleResolution.Disabled => (ErrorCodes.InvalidRequest,
                $"The method '{methodName}' is found but the namespace '{module}' is disabled for {context.Url?.ToString() ?? "n/a"}. Consider adding the namespace '{module}' to JsonRpc.AdditionalRpcUrls for an additional URL, or to JsonRpc.EnabledModules for the default URL."),
            ModuleResolution.EndpointDisabled => (ErrorCodes.InvalidRequest,
                $"The method '{methodName}' is found in namespace '{module}' for {context.Url?.ToString() ?? "n/a"}' but is disabled for {context.RpcEndpoint}."),
            ModuleResolution.NotAuthenticated => (ErrorCodes.InvalidRequest, $"The method '{methodName}' must be authenticated."),
            _ => (null, null)
        };
    }
}
