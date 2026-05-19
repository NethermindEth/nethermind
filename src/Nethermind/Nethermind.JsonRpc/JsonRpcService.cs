// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
    private readonly ILogger _logger = logManager.GetClassLogger<JsonRpcService>();
    private readonly IRpcModuleProvider _rpcModuleProvider = rpcModuleProvider;
    private readonly HashSet<string> _methodsLoggingFiltering = (jsonRpcConfig.MethodsLoggingFiltering ?? []).ToHashSet();
    private readonly Lock _propertyInfoModificationLock = new();
    private readonly int _maxLoggedRequestParametersCharacters = jsonRpcConfig.MaxLoggedRequestParametersCharacters ?? int.MaxValue;

    private Dictionary<TypeAsKey, PropertyInfo?> _propertyInfoCache = [];

    public ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
    {
        (int? errorCode, string errorMessage) = Validate(rpcRequest, context);
        if (errorCode.HasValue)
        {
            if (_logger.IsDebug) _logger.Debug($"Validation error when handling request: {rpcRequest}");
            return ValueTask.FromResult<JsonRpcResponse>(GetErrorResponse(rpcRequest.Method, errorCode.Value, errorMessage, null, rpcRequest.Id));
        }

        try
        {
            ValueTask<JsonRpcResponse> responseTask = ExecuteRequestAsync(rpcRequest, context);
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

        (int errorCode, string errorText) = ex switch
        {
            LimitExceededException or ConcurrencyLimitReachedException => (ErrorCodes.LimitExceeded, "Too many requests"),
            ModuleRentalTimeoutException => (ErrorCodes.ModuleTimeout, "Timeout"),
            _ => (ErrorCodes.InternalError, "Internal error"),
        };

        if (_logger.IsError) _logger.Error($"Error during method execution, request: {rpcRequest}", ex);
        return GetErrorResponse(rpcRequest.Method, errorCode, errorText, ex.ToString(), rpcRequest.Id);
    }

    private ValueTask<JsonRpcResponse> ExecuteRequestAsync(JsonRpcRequest rpcRequest, JsonRpcContext context)
    {
        string methodName = rpcRequest.Method.Trim();

        ResolvedMethodInfo? result = _rpcModuleProvider.Resolve(methodName);
        return result?.MethodInfo is not null
            ? ExecuteAsync(rpcRequest, methodName, result, context)
            : ValueTask.FromResult<JsonRpcResponse>(GetErrorResponse(methodName, ErrorCodes.MethodNotFound, "Method not found", $"{rpcRequest.Method}", rpcRequest.Id));
    }

    private async ValueTask<JsonRpcResponse> ExecuteAsync(JsonRpcRequest request, string methodName, ResolvedMethodInfo method, JsonRpcContext context)
    {
        const string GetLogsMethodName = "eth_getLogs";

        JsonRpcErrorResponse? value = PrepareParameters(request, methodName, method, out object[]? parameters, out bool hasMissing);
        if (value is not null)
        {
            return value;
        }

        IRpcModule rpcModule = await _rpcModuleProvider.Rent(methodName, method.ReadOnly);
        if (rpcModule is IContextAwareRpcModule contextAwareModule)
        {
            contextAwareModule.Context = context;
        }
        bool returnImmediately = methodName != GetLogsMethodName;
        Action? returnAction = returnImmediately ? null : () => _rpcModuleProvider.Return(methodName, rpcModule);
        IResultWrapper? resultWrapper = null;
        try
        {
            // Execute method
            object invocationResult = hasMissing ?
                method.MethodInfo.Invoke(rpcModule, parameters) :
                method.Invoker.Invoke(rpcModule, new Span<object?>(parameters));

            switch (invocationResult)
            {
                case IResultWrapper wrapper:
                    resultWrapper = wrapper;
                    break;
                case Task task:
                    await task;
                    resultWrapper = GetResultProperty(task)?.GetValue(task) as IResultWrapper;
                    break;
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
                _rpcModuleProvider.Return(methodName, rpcModule);
            }
        }

        if (resultWrapper is null)
        {
            return HandleMissingResultWrapper(request, methodName, returnAction);
        }

        Result result = resultWrapper.Result;
        return result.ResultType != ResultType.Success
            ? GetErrorResponse(methodName, resultWrapper.ErrorCode, result.Error, resultWrapper.Data, request.Id, returnAction, resultWrapper.IsTemporary)
            : GetSuccessResponse(methodName, resultWrapper.Data, request.Id, returnAction);

        [MethodImpl(MethodImplOptions.NoInlining)]
        JsonRpcResponse HandleMissingResultWrapper(JsonRpcRequest request, string methodName, Action returnAction)
        {
            string errorMessage = $"Method {methodName} execution result does not implement IResultWrapper";
            if (_logger.IsError) _logger.Error(errorMessage);
            return GetErrorResponse(methodName, ErrorCodes.InternalError, errorMessage, null, request.Id, returnAction);
        }
    }

    private JsonRpcErrorResponse? PrepareParameters(JsonRpcRequest request, string methodName, ResolvedMethodInfo method, out object[]? parameters, out bool hasMissing)
    {
        parameters = null;
        hasMissing = false;
        ReadOnlyMemory<byte> providedParametersUtf8 = request.ParamsUtf8;
        bool useUtf8Parameters = CanDeserializeParametersFromUtf8(request, method.ExpectedParameters);
        JsonElement providedParameters = useUtf8Parameters ? default : request.Params;

        if (_logger.IsTrace)
        {
            if (useUtf8Parameters)
            {
                LogRequest(methodName, providedParametersUtf8, method.ExpectedParameters);
            }
            else
            {
                LogRequest(methodName, providedParameters, method.ExpectedParameters);
            }
        }

        int providedParametersLength = useUtf8Parameters
            ? JsonRpcArrayReader.CountItems(providedParametersUtf8)
            : providedParameters.ValueKind == JsonValueKind.Array ? providedParameters.GetArrayLength() : 0;
        int missingParamsCount = method.ExpectedParameters.Length - providedParametersLength;
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
            method.ExpectedParameters,
            methodName,
            request.Id,
            providedParametersLength,
            ref missingParamsCount);
        if (validationError is not null)
        {
            return validationError;
        }

        if (method.ExpectedParameters.Length == 0)
        {
            return null;
        }

        try
        {
            (parameters, hasMissing) = useUtf8Parameters
                ? DeserializeParameters(method.ExpectedParameters, providedParametersLength, providedParametersUtf8, missingParamsCount)
                : DeserializeParameters(method.ExpectedParameters, providedParametersLength, providedParameters, providedParametersUtf8, missingParamsCount);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"Incorrect JSON RPC parameters when calling {methodName} with params [{GetParamsForLog(request)}] {e}");
            return GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", null, request.Id);
        }

        return null;
    }

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

            TargetInvocationException and { InnerException: JsonException } =>
                GetErrorResponse(methodName, ErrorCodes.InvalidParams, "Invalid params", ex.InnerException?.ToString(), request.Id, returnAction),

            OperationCanceledException or { InnerException: OperationCanceledException } =>
                GetErrorResponse(methodName, ErrorCodes.Timeout,
                    $"{methodName} request was canceled due to enabled timeout.", null, request.Id, returnAction),

            LimitExceededException or ConcurrencyLimitReachedException
                or { InnerException: LimitExceededException }
                or { InnerException: ConcurrencyLimitReachedException } =>
                GetErrorResponse(methodName, ErrorCodes.LimitExceeded, "Too many requests", null, request.Id, returnAction),

            { InnerException: InsufficientBalanceException } =>
                GetErrorResponse(methodName, ErrorCodes.InvalidInput, ex.InnerException.Message, ex.ToString(), request.Id, returnAction),

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

        JsonRpcErrorResponse HandleMissingTrieNode(MissingTrieNodeException ex, string methodName, JsonRpcRequest request, Action? returnAction)
        {
            // HasStateForBlock only checks the state root; subtree nodes can still be pruned out
            // after a successful guard. Surface as -32000 (Geth wire parity) and warn so operators
            // can investigate whether it's a legitimate pruning gap or a deeper issue.
            if (_logger.IsWarn) _logger.Warn($"Missing trie node during {methodName}: {ex.Message}");
            return GetErrorResponse(methodName, ErrorCodes.ResourceNotFound, ex.Message, ex.ToString(), request.Id, returnAction);
        }
    }

    private PropertyInfo? GetResultProperty(Task task)
    {
        Type type = task.GetType();
        if (_propertyInfoCache.TryGetValue(type, out PropertyInfo? value))
        {
            return value;
        }

        return GetResultPropertySlow(type);
    }

    private PropertyInfo? GetResultPropertySlow(Type type)
    {
        lock (_propertyInfoModificationLock)
        {
            Dictionary<TypeAsKey, PropertyInfo?> current = _propertyInfoCache;
            // Re-check inside the lock in case another thread already added it
            if (current.TryGetValue(type, out PropertyInfo? value))
            {
                return value;
            }

            // Copy-on-write: create a new dictionary so we don't mutate
            // the one other threads may be reading without locks.
            Dictionary<TypeAsKey, PropertyInfo?> propertyInfoCache = new(current);
            PropertyInfo? propertyInfo = type.GetProperty("Result");
            propertyInfoCache[type] = propertyInfo;

            // Publish the new cache instance atomically by swapping the reference.
            // Readers grabbing _propertyInfoCache will now see the updated dictionary.
            _propertyInfoCache = propertyInfoCache;

            return propertyInfo;
        }
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
                : Type.Missing;
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
                : Type.Missing;
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

        Utf8JsonReader parameterReader = new(providedParameterUtf8.Span, isFinalBlock: true, state: default);
        JsonTypeInfo? typeInfo = expectedParameter.TypeInfo;
        return typeInfo is not null
            ? JsonSerializer.Deserialize(ref parameterReader, typeInfo)
            : JsonSerializer.Deserialize(ref parameterReader, expectedParameter.ParameterType, EthereumJsonSerializer.JsonOptions);

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

    private static (object[]? parameters, bool hasMissing) DeserializeParameters(
        ExpectedParameter[] expectedParameters,
        int providedParametersLength,
        ReadOnlyMemory<byte> providedParametersUtf8,
        int missingParamsCount)
    {
        int totalLength = providedParametersLength + missingParamsCount;
        if (totalLength == 0) return ([], false);

        object[] executionParameters = new object[totalLength];

        bool hasMissing = missingParamsCount != 0;
        int i = 0;

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
                hasMissing |= ReferenceEquals(parameter, Type.Missing);
                i++;
            }

            if (i != providedParametersLength)
            {
                ThrowMismatchedParameterCount();
            }
        }

        for (i = providedParametersLength; i < totalLength; i++)
        {
            executionParameters[i] = Type.Missing;
        }

        return (executionParameters, hasMissing);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowMismatchedParameterCount() =>
            throw new JsonException("Mismatched JSON-RPC parameter count.");
    }

    private static (object[]? parameters, bool hasMissing) DeserializeParameters(
        ExpectedParameter[] expectedParameters,
        int providedParametersLength,
        JsonElement providedParameters,
        ReadOnlyMemory<byte> providedParametersUtf8,
        int missingParamsCount)
    {
        int totalLength = providedParametersLength + missingParamsCount;
        if (totalLength == 0) return ([], false);

        object[] executionParameters = new object[totalLength];

        bool hasMissing = missingParamsCount != 0;
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
                hasMissing |= ReferenceEquals(parameter, Type.Missing);
                i++;
            }
        }

        for (i = providedParametersLength; i < totalLength; i++)
        {
            executionParameters[i] = Type.Missing;
        }

        return (executionParameters, hasMissing);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowMissingParameterBytes() =>
            throw new JsonException("Missing JSON-RPC parameter bytes.");
    }

    private static JsonRpcResponse GetSuccessResponse(string methodName, object result, JsonRpcId id, Action? disposableAction)
    {
        JsonRpcResponse response = new JsonRpcSuccessResponse(disposableAction)
        {
            Result = result,
            Id = id,
            MethodName = methodName
        };

        return response;
    }

    public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, object? id = null, string? methodName = null) =>
        GetErrorResponse(methodName ?? string.Empty, errorCode, errorMessage, null, JsonRpcId.FromObject(id));

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

    private (int? ErrorType, string ErrorMessage) Validate(JsonRpcRequest? rpcRequest, JsonRpcContext context)
    {
        if (rpcRequest is null)
        {
            return (ErrorCodes.InvalidRequest, "Invalid request");
        }

        string methodName = rpcRequest.Method;
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return (ErrorCodes.InvalidRequest, "Method is required");
        }

        methodName = methodName.Trim();

        ModuleResolution result = _rpcModuleProvider.Check(methodName, context, out string? module);
        if (result == ModuleResolution.Enabled)
        {
            return (null, null);
        }

        return GetErrorResult(methodName, context, result, module);

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
